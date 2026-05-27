using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using Azure;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Handles restoring files from Azure Blob Storage.
/// Supports both individual file and full restore operations.
/// Uses parallel downloads with bounded memory for optimal performance.
/// </summary>
public partial class RestoreService
{
    private readonly LocalDatabaseService _databaseService;
    private readonly IBlobStorageService _blobService;
    private readonly EncryptionService _encryptionService;
    
    // Parallel download settings - increased for better bandwidth utilization
    // Default concurrency for moderate chunk sizes; adaptive method below adjusts for extremes.
    // Memory bounded by: effectiveConcurrency * maxChunkSize * ~2 copies
    private const int DefaultParallelChunkDownloads = 12;

    // File-level parallelism for multi-file restore operations
    // 16 files x 12 chunks = 192 concurrent HTTP downloads max
    // Azure single-account egress limit is 50 Gbps; 16 files better saturates bandwidth.
    private const int MaxParallelFileRestores = 16;

    // Chunk-level retry settings for transient Azure failures (503, timeouts)
    private const int MaxChunkRetries = 3;
    private const int ChunkRetryBaseDelayMs = 500;

    // Large FileStream buffer — reduces syscalls for multi-MB chunk writes
    private const int LargeFileStreamBufferSize = 4 * 1024 * 1024; // 4 MB

    // Memory budget overhead for concurrent FileStream buffers during parallel restores
    private const long FileStreamOverhead = (long)MaxParallelFileRestores * LargeFileStreamBufferSize;

    // Machine-adaptive parallelism for local I/O + CPU operations (e.g., SHA-256 hashing).
    // Uses logical processor count (cores × threads-per-core on hyperthreaded CPUs).
    // Not used for Azure-network-bound operations — those have fixed constants
    // tuned to Azure throughput limits, not local machine capability.
    private static readonly int LocalIoParallelism = Math.Clamp(Environment.ProcessorCount, 2, 64);

    // Cached sensitive directories for ValidateRestorePath — avoids repeated
    // Environment.GetFolderPath calls across thousands of file restores
    private static readonly string[] SensitiveDirectories = GetSensitiveDirectories();

    private static string[] GetSensitiveDirectories()
    {
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
        }.Where(d => !string.IsNullOrEmpty(d)).ToArray();
    }

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Directory where per-file .diag logs are written on error.
    /// Set by the UI layer. When null, uses the system temp directory.
    /// </summary>
    public string? DiagnosticsDirectory { get; set; }

    /// <summary>
    /// Optional throughput metrics logger. When set, per-file and operation-level
    /// metrics are recorded to JSONL files for post-hoc performance analysis.
    /// </summary>
    public ThroughputMetrics? Metrics { get; set; }

    /// <summary>
    /// Event for detailed debug/diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;
    
    [Conditional("DIAGNOSTICLOG")]
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [Restore] {message}");
    }

    /// <summary>
    /// Computes adaptive chunk-level parallelism based on max chunk size.
    /// Large chunks (64 MB) use fewer concurrent downloads to limit memory,
    /// while small chunks (256 KB) use more to overcome HTTP latency.
    /// </summary>
    private static int ComputeAdaptiveChunkConcurrency(int maxChunkBytes)
    {
        // Target ~384 MB of in-flight chunk data. Each download peaks at ~2× chunk size
        // (encrypted + plaintext overlap briefly during DecryptInto), giving ~768 MB peak
        // memory at full concurrency — equivalent to the original ×3 calibration.
        // The MemoryBudget provides the real memory safety net; this heuristic is a
        // secondary defense that caps HTTP connections for non-budget-limited scenarios.
        const long TargetInFlightBytes = 384L * 1024 * 1024;
        var computed = (int)(TargetInFlightBytes / Math.Max(maxChunkBytes, 1));
        return Math.Clamp(computed, 4, 24);
    }

    /// <summary>
    /// Validates that a restore path is safe and not targeting sensitive system directories.
    /// </summary>
    private static void ValidateRestorePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Validate the path doesn't contain invalid characters (check before normalization)
        var invalidChars = Path.GetInvalidPathChars();
        if (path.IndexOfAny(invalidChars) >= 0)
        {
            throw new SecurityPolicyException(
                "Invalid restore path: contains invalid characters",
                SecurityPolicyType.InvalidBlobName);
        }

        // Get the full path to normalize it and resolve any relative components
        var fullPath = Path.GetFullPath(path);

        // Check for path traversal attempts by looking for ".." as a directory segment
        // in the original path. Path.GetFullPath resolves these, so if the original
        // contains ".." segments, someone is attempting traversal.
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(s => s == ".."))
        {
            throw new SecurityPolicyException(
                "Invalid restore path: path traversal detected",
                SecurityPolicyType.InvalidBlobName);
        }
        
        // Check against cached sensitive system directories
        foreach (var sensitiveDir in SensitiveDirectories)
        {
            if (fullPath.StartsWith(sensitiveDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityPolicyException(
                    $"Cannot restore to protected system directory: {sensitiveDir}",
                    SecurityPolicyType.InvalidBlobName);
            }
        }
    }

    public RestoreService(
        LocalDatabaseService databaseService,
        IBlobStorageService blobService,
        EncryptionService encryptionService)
    {
        ArgumentNullException.ThrowIfNull(databaseService);
        ArgumentNullException.ThrowIfNull(blobService);
        ArgumentNullException.ThrowIfNull(encryptionService);

        _databaseService = databaseService;
        _blobService = blobService;
        _encryptionService = encryptionService;
        Log("RestoreService initialized");
    }

    /// <summary>
    /// Lists all files available for restore from Azure.
    /// Downloads metadata blobs in parallel for faster loading.
    /// </summary>
    public async Task<List<BackedUpFile>> ListRestorableFilesAsync(
        IProgress<(int completed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Bail before raising StatusChanged so a cancelled call doesn't
        // emit a misleading "Retrieving..." right before the OperationCanceledException.
        cancellationToken.ThrowIfCancellationRequested();

        Log("ListRestorableFilesAsync: Starting to list restorable files");

        StatusChanged?.Invoke(this, "Retrieving file list from Azure...");

        var wrappedProgress = progress != null
            ? new Progress<(int completed, int total)>(p =>
            {
                progress.Report(p);
                if (p.completed % 50 == 0 || p.completed == p.total)
                {
                    StatusChanged?.Invoke(this, $"Loading file metadata... {p.completed:N0}/{p.total:N0}");
                }
            })
            : null;

        var files = await _blobService.LoadAllFileMetadataAsync(wrappedProgress, cancellationToken: cancellationToken);

        Log($"ListRestorableFilesAsync: Loaded {files.Count} file metadata entries");
        StatusChanged?.Invoke(this, $"Found {files.Count} files available for restore");
        return files;
    }

    /// <summary>
    /// Restores a single file to the specified location.
    /// Verifies integrity after restore by comparing file hash.
    /// Uses parallel chunk downloads for files with multiple chunks.
    /// </summary>
    /// <param name="memoryBudget">Optional shared memory budget for throttling parallel chunk downloads.
    /// When null, a per-file budget is created from config. Pass a shared instance for
    /// multi-file operations so total in-flight memory stays within the user's limit.</param>
    /// <param name="bandwidthScheduler">Optional batch-level <see cref="BandwidthScheduler"/>.
    /// When provided, every successful chunk write feeds the AIMD throughput
    /// signal and every transient HTTP retry / detected stall halves
    /// file-level concurrency. Single-file restores from the UI pass null
    /// because no batch-level scheduling decision exists.</param>
    /// <param name="largeChunkPool">B71 (W5 Phase 3 Commit 3): optional
    /// operation-scope <see cref="ChunkBufferPool"/> built with
    /// <see cref="ChunkBufferPool.LargeChunkBucketSizes"/>. When supplied,
    /// downloaded plaintext buffers for chunks at or above
    /// <see cref="ChunkingService.PoolSkipThresholdBytes"/> are rented
    /// from this pool instead of <see cref="System.Buffers.ArrayPool{T}.Shared"/>,
    /// keeping restore-side residency inside the budget's accounting.
    /// Single-file restores from the UI pass null and fall back to the
    /// shared ArrayPool.</param>
    /// <param name="smallChunkPool">B71: optional operation-scope
    /// <see cref="ChunkBufferPool"/> built with
    /// <see cref="ChunkBufferPool.SmallChunkBucketSizes"/>. When supplied,
    /// downloaded plaintext buffers for chunks below
    /// <see cref="ChunkingService.PoolSkipThresholdBytes"/> are rented
    /// from this pool. Null falls back to the shared ArrayPool.</param>
    public async Task<bool> RestoreFileAsync(
        BackedUpFile file,
        string? restorePath = null,
        bool overwriteExisting = false,
        IProgress<(long current, long total)>? progress = null,
        MemoryBudget? memoryBudget = null,
        CancellationToken cancellationToken = default,
        BandwidthScheduler? bandwidthScheduler = null,
        ChunkBufferPool? largeChunkPool = null,
        ChunkBufferPool? smallChunkPool = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        var diag = new FileOperationDiagnostics(file.LocalPath, "Restore", DiagnosticsDirectory);
        using var _ = diag.SetAmbient();
        var restoreFileStopwatch = Stopwatch.StartNew();
        Log($"RestoreFileAsync: Starting restore of '{Path.GetFileName(file.LocalPath)}' ({file.FileSize} bytes, {file.Chunks.Count} chunks)");
        diag.Record($"File: size={file.FileSize:N0}, chunks={file.Chunks.Count}, hash={file.FileHash?[..8]}..., status={file.Status}");
        
        var targetPath = restorePath ?? file.LocalPath;
        
        // Validating restore path for security
        Log($"RestoreFileAsync: Validating path: {targetPath}");
        ValidateRestorePath(targetPath);
        
        var tempPath = targetPath + ".tmp";
        
        try
        {
            StatusChanged?.Invoke(this, $"Restoring: {Path.GetFileName(targetPath)}");

            // Check if file exists
            if (File.Exists(targetPath) && !overwriteExisting)
            {
                Log($"RestoreFileAsync: File already exists and overwrite=false: {targetPath}");
                ErrorOccurred?.Invoke(this, $"File already exists: {targetPath}");
                return false;
            }

            // Create directory if needed
            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                Log($"RestoreFileAsync: Created directory: {directory}");
            }
            
            // Validate chunk sequence before starting restore
            Log("RestoreFileAsync: Validating chunk sequence");
            var sortedChunks = file.Chunks.OrderBy(c => c.Index).ToList();
            for (int i = 0; i < sortedChunks.Count; i++)
            {
                if (sortedChunks[i].Index != i)
                {
                    throw new DataIntegrityException(
                        $"Invalid chunk sequence: expected index {i}, found {sortedChunks[i].Index}",
                        file.LocalPath);
                }
            }
            
            // Validate total chunk size matches file size
            var totalChunkSize = sortedChunks.Sum(c => (long)c.Length);
            if (totalChunkSize != file.FileSize)
            {
                throw new DataIntegrityException(
                    $"Chunk size mismatch: chunks total {totalChunkSize} bytes, expected {file.FileSize} bytes",
                    file.LocalPath);
            }
        var maxChunkLen = sortedChunks.Max(c => c.Length);
            var minChunkLen = sortedChunks.Min(c => c.Length);
            Log($"RestoreFileAsync: Chunk validation passed, downloading {sortedChunks.Count} chunks " +
                $"(min={minChunkLen:N0}, max={maxChunkLen:N0} bytes), " +
                $"GC.TotalMemory={GC.GetTotalMemory(false):N0}");

            long currentBytes = 0;
            string restoredHash;

            if (sortedChunks.Count > 1)
            {
                // B41: capture into a non-null local with ?? so flow analysis
                // can prove non-null at the dereferences below. The pre-B41
                // shape stored the null-check result in a separate
                // `ownsMemoryBudget` flag and reassigned `memoryBudget` inside
                // an if-block, which CS8602 could not follow across the
                // indirection. The ownership semantics are unchanged: when
                // the caller passed null we own the lifetime and dispose in
                // the finally; when the caller passed an instance we leave
                // disposal to them.
                //
                // Use the caller's shared budget when provided (multi-file restores),
                // otherwise create a per-file budget from config (single-file restore from UI).
                var ownsMemoryBudget = memoryBudget == null;
                var budget = memoryBudget ?? MemoryBudget.FromConfig(_databaseService.GetConfiguration());

                Log($"RestoreFileAsync: Using bounded parallel downloads (adaptive concurrency, " +
                    $"memoryBudget={(!budget.IsUnlimited ? FormatHelper.FormatBytes(budget.TotalBytes) : "unlimited")}, " +
                    $"shared={!ownsMemoryBudget})");
                try
                {
                    // Use bounded producer-consumer pattern with channels
                    // Hash is computed incrementally as chunks are written in order — no file re-read needed
                    restoredHash = await RestoreWithBoundedParallelDownloadsAsync(
                        sortedChunks, file, tempPath, budget, progress, 
                        p => currentBytes = p, diag, bandwidthScheduler,
                        largeChunkPool, smallChunkPool, cancellationToken);
                }
                finally
                {
                    if (ownsMemoryBudget)
                        budget.Dispose();
                }
            }
            else
            {
                Log("RestoreFileAsync: Single chunk, downloading directly");
                diag.Record("Single-chunk restore path");
                // Single chunk - download, write, and compute hash in one pass
                using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using FileStream outputStream = new(tempPath, FileMode.Create, FileAccess.Write, 
                    FileShare.None, bufferSize: 81920, useAsync: true);

                foreach (var chunk in sortedChunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Log($"RestoreFileAsync: Downloading chunk {chunk.Index} ({chunk.Length} bytes) blob={chunk.BlobName}");

                    // Download with retry for transient Azure failures — same pattern as multi-chunk path.
                    // Single-chunk files are especially vulnerable to transient 503s
                    // at high concurrency since they have no other chunks to amortize a failure across.
                    byte[]? chunkData = null;
                    for (var attempt = 0; attempt <= MaxChunkRetries; attempt++)
                    {
                        try
                        {
                            chunkData = await _blobService.DownloadChunkAsync(chunk.BlobName, cancellationToken);
                            break;
                        }
                        catch (Exception ex) when (attempt < MaxChunkRetries && IsTransientError(ex))
                        {
                            // B63: feed the AIMD scheduler from the single-chunk path too. Without
                            // this, multi-chunk-adjacent files (a 17 MB file split into a single
                            // 17 MB chunk vs a 14 MB file split into a single 14 MB chunk) skip the
                            // signal entirely and the scheduler can keep adding capacity while the
                            // server is already 503'ing single-chunk requests.
                            bandwidthScheduler?.NotifyTransientError();
                            var delay = ChunkRetryBaseDelayMs * (1 << attempt);
                            diag.RecordChunk("TransientRetry", chunk.Index, chunk.Hash, chunk.Length,
                                extra: $"attempt={attempt + 1}/{MaxChunkRetries + 1}, error={ex.GetType().Name}");
                            Log($"RestoreFileAsync: Transient error on single chunk {chunk.Index} " +
                                $"(attempt {attempt + 1}/{MaxChunkRetries + 1}): {ex.GetType().Name}: {ex.Message}, " +
                                $"retrying in {delay}ms");
                            await Task.Delay(delay, cancellationToken);
                        }
                    }

                    // Verify chunk size and hash match metadata
                    Log($"RestoreFileAsync: Verifying chunk {chunk.Index} integrity");
                    diag.RecordChunk("Downloaded", chunk.Index, chunk.Hash, chunkData!.Length,
                        extra: $"blob={chunk.BlobName}, expectedLen={chunk.Length}");
                    VerifyChunkIntegrity(chunkData!, chunk, file.LocalPath);
                    diag.RecordChunk("Verified", chunk.Index, chunk.Hash,
                        chunkData.Length, extra: "hash+size OK");

                    await outputStream.WriteAsync(chunkData, cancellationToken);
                    incrementalHash.AppendData(chunkData);

                    currentBytes += chunk.Length;
                    progress?.Report((currentBytes, file.FileSize));
                    // B63: feed scheduler from single-chunk success path too so that
                    // even single-chunk-large-file restores contribute to the EWMA
                    // throughput signal that drives the next batch's dispatch wave.
                    bandwidthScheduler?.RecordBytesCompleted(chunk.Length);
                }

                await outputStream.FlushAsync(cancellationToken);
                restoredHash = Convert.ToHexString(incrementalHash.GetHashAndReset());
            }

            // Verify restored file integrity using incrementally computed hash
            // (no file re-read needed — hash was computed as chunks were written in order)
            Log($"RestoreFileAsync: Verifying file integrity (incremental hash): {restoredHash[..8]}...");
            diag.Record($"File hash verification: computed={restoredHash[..12]}..., expected={file.FileHash?[..12]}...");
            StatusChanged?.Invoke(this, $"Verifying integrity: {Path.GetFileName(targetPath)}");

            if (!string.Equals(restoredHash, file.FileHash, StringComparison.Ordinal))
            {
                Log($"RestoreFileAsync: HASH MISMATCH! expected={file.FileHash}, got={restoredHash}");
                diag.Record($"[HASH MISMATCH] expected={file.FileHash}, computed={restoredHash}, " +
                    $"chunks={file.Chunks.Count}, fileSize={file.FileSize:N0}");
                // Delete corrupted temp file
                FileSystemHelper.TryDelete(tempPath);

                throw new DataIntegrityException(
                    $"File hash mismatch after restore: expected {file.FileHash}, got {restoredHash}",
                    file.LocalPath);
            }
            
            // Move temp file to final destination (atomic on same filesystem).
            // Pre-Move conflict delete kept as raw File.Delete: the immediately-
            // following Move would itself throw if this fails, so a swallow here
            // would just defer the same error.
            Log($"RestoreFileAsync: Moving temp file to final destination: {targetPath}");
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            File.Move(tempPath, targetPath);

            // Set file timestamps
            File.SetLastWriteTimeUtc(targetPath, file.LastModified);

            Log($"RestoreFileAsync: Successfully restored '{Path.GetFileName(targetPath)}' ({file.FileSize} bytes), " +
                $"GC.TotalMemory={GC.GetTotalMemory(false):N0}");
            StatusChanged?.Invoke(this, $"Restored and verified: {Path.GetFileName(targetPath)}");

            // Record per-file metrics for single-chunk files (multi-chunk metrics are recorded
            // inside RestoreWithBoundedParallelDownloadsAsync)
            if (sortedChunks.Count == 1)
            {
                restoreFileStopwatch.Stop();
                var singleElapsed = restoreFileStopwatch.Elapsed.TotalSeconds;
                Metrics?.RecordFile(new FileMetrics
                {
                    Operation = "restore",
                    Path = file.LocalPath,
                    Bytes = file.FileSize,
                    Chunks = 1,
                    ChunkMin = sortedChunks[0].Length,
                    ChunkMax = sortedChunks[0].Length,
                    ElapsedSeconds = singleElapsed,
                    ThroughputMBps = singleElapsed > 0 ? file.FileSize / singleElapsed / (1024 * 1024) : 0,
                    EffectiveConcurrency = 1
                });
            }

            return true;
        }
        catch (DataIntegrityException ex)
        {
            Log($"RestoreFileAsync: DataIntegrityException: {ex.Message}, " +
                $"file='{file.LocalPath}', size={file.FileSize:N0}, chunks={file.Chunks.Count}, " +
                $"GC.TotalMemory={GC.GetTotalMemory(false):N0}");
            diag.RecordError("RestoreFileAsync.DataIntegrity", ex);
            var diagPath = diag.Flush($"Data integrity failure: {ex.Message}");
            if (diagPath != null)
            {
                Log($"RestoreFileAsync: Diagnostics written to {diagPath}");
            }
            // Clean up temp file before re-throwing so callers (e.g., corrupted recovery)
            // don't leave orphaned .tmp files on disk
            FileSystemHelper.TryDelete(tempPath);
            throw; // Re-throw integrity exceptions
        }
        catch (OperationCanceledException)
        {
            Log("RestoreFileAsync: Operation cancelled");
            // B24: cancellation is a user-initiated outcome, NOT an error
            // worth surfacing as a .diag file. Mirrors the B23 fix in
            // BackupOrchestrator.BackupFileAsync. Without this Discard,
            // an in-flight Restore at Cancel time leaves the diag in
            // the live registry and the ProcessExit hook writes a
            // stale partial snapshot to disk.
            diag.Discard();
            FileSystemHelper.TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            Log($"RestoreFileAsync: EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            Log($"RestoreFileAsync: StackTrace: {ex.StackTrace}");
            Log($"RestoreFileAsync: Context - file='{file.LocalPath}', size={file.FileSize:N0}, " +
                $"chunks={file.Chunks.Count}, target='{targetPath}', " +
                $"GC.TotalMemory={GC.GetTotalMemory(false):N0}");
            if (ex is IOException ioEx)
            {
                Log($"RestoreFileAsync: IOException HResult=0x{ioEx.HResult:X8}, " +
                    $"tempExists={File.Exists(tempPath)}, targetExists={File.Exists(targetPath)}");
            }
            diag.RecordError("RestoreFileAsync.General", ex);
            var diagPath = diag.Flush($"Restore failed: {ex.Message}");
            if (diagPath != null)
            {
                Log($"RestoreFileAsync: Diagnostics written to {diagPath}");
            }
            // Clean up temp file on error
            FileSystemHelper.TryDelete(tempPath);

            ErrorOccurred?.Invoke(this, $"Failed to restore {file.LocalPath}: {ex.Message}");
            return false;
        }
        finally
        {
            // B24: belt-and-braces -- the success path returns true above
            // without throwing, leaving diag registered in the static
            // _live set. Discard removes it so the AppDomain.ProcessExit
            // hook does NOT write a stale snapshot for a file that
            // restored cleanly. Idempotent: calling Discard after Flush
            // (the catch paths above) is a no-op via the _isFlushed
            // Interlocked guard.
            diag.Discard();
        }
    }
    
    /// <summary>
    /// Verifies downloaded chunk data matches expected hash and size.
    /// Accepts ReadOnlySpan to support both exact-sized arrays and sliced pooled buffers.
    /// </summary>
    private void VerifyChunkIntegrity(ReadOnlySpan<byte> chunkData, ChunkInfo chunk, string filePath)
    {
        // Verify chunk size matches metadata
        if (chunkData.Length != chunk.Length)
        {
            throw new DataIntegrityException(
                $"Chunk {chunk.Index} size mismatch: got {chunkData.Length} bytes, expected {chunk.Length} bytes",
                filePath);
        }

        // Verify chunk hash matches metadata
        var actualHash = HashHelper.ComputeHash(chunkData);
        if (!string.Equals(actualHash, chunk.Hash, StringComparison.Ordinal))
        {
            throw new DataIntegrityException(
                $"Chunk {chunk.Index} hash mismatch: expected {chunk.Hash}, got {actualHash}",
                filePath);
        }
    }

    /// <summary>
    /// B71 (W5 Phase 3 Commit 3): return a plaintext chunk buffer to the
    /// source it was rented from. When <paramref name="sourcePool"/> is
    /// non-null the buffer was rented from a restore-scope
    /// <see cref="ChunkBufferPool"/>; otherwise it was rented from
    /// <see cref="ArrayPool{T}.Shared"/>. Routing through this helper at
    /// every return site keeps the ownership rule in one place and prevents
    /// a buffer rented from one pool from accidentally being returned to a
    /// different pool's bucket geometry.
    /// </summary>
    private static void ReturnPlaintextBuffer(byte[] buffer, ChunkBufferPool? sourcePool)
    {
        if (sourcePool is not null)
            sourcePool.Return(buffer);
        else
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }

    /// <summary>
    /// Determines whether an exception represents a transient Azure error worth retrying.
    /// Covers HTTP 408/429/500/502/503/504 from Azure, I/O timeouts, and TaskCanceledException
    /// caused by HttpClient timeouts (not user cancellation).
    /// </summary>
    private static bool IsTransientError(Exception ex)
    {
        // Azure SDK wraps transient HTTP errors in RequestFailedException
        if (ex is Azure.RequestFailedException rfe)
        {
            return rfe.Status is 408 or 429 or 500 or 502 or 503 or 504;
        }

        // HttpClient timeout surfaces as TaskCanceledException with an inner TimeoutException
        if (ex is TaskCanceledException { InnerException: TimeoutException })
            return true;

        // Network-level I/O failures
        if (ex is IOException or System.Net.Http.HttpRequestException)
            return true;

        return false;
    }

    /// <summary>
    /// Restores a file using bounded parallel downloads with a producer-consumer pattern.
    /// Downloads chunks in parallel but writes them sequentially, limiting memory usage.
    /// Chunk-level concurrency is adaptive based on chunk size (large chunks → fewer concurrent,
    /// small chunks → more concurrent) to balance memory pressure vs HTTP latency.
    /// Includes retry with exponential backoff for transient Azure failures.
    /// Computes the file hash incrementally as chunks are written in order,
    /// eliminating the need to re-read the entire file from disk for verification.
    /// </summary>
    /// <returns>The SHA-256 hash of the restored file, computed incrementally during write.</returns>
    /// <remarks>
    /// B62 (C): the producer/consumer pipeline uses an in-order chunk window
    /// rather than a producer-released throttle. The <c>windowSlots</c>
    /// semaphore is acquired by the producer BEFORE budget acquire and
    /// released by the writer AFTER each successful chunk write. This bounds
    /// the reorder buffer depth to exactly <c>effectiveConcurrency</c> chunks
    /// and structurally breaks the pre-B62 deadlock where buffered higher-
    /// index chunks held plaintext budget while the next-needed chunk parked
    /// in <see cref="MemoryBudget.AcquireAsync"/>: the next chunk a producer
    /// can dispatch is bounded by what the writer has consumed, so a
    /// dispatched chunk is always at most window-size away from being
    /// written, and the producer cannot accumulate more than
    /// <c>effectiveConcurrency</c> chunks ahead of the writer.
    /// </remarks>
    /// <param name="bandwidthScheduler">Optional batch-level AIMD controller.
    /// When provided, every successful chunk write feeds its EWMA throughput
    /// signal, every transient HTTP retry fires a multiplicative-decrease
    /// fast path, and a deadlock-suspect watchdog snapshot triggers an
    /// immediate decrease. Pass <see langword="null"/> for single-file
    /// restores from the UI where no batch scheduler exists.</param>
    /// <param name="largeChunkPool">B71 (W5 Phase 3 Commit 3): optional
    /// large-geometry plaintext-buffer recycler. See
    /// <see cref="RestoreFileAsync"/> for the contract.</param>
    /// <param name="smallChunkPool">B71: optional small-geometry plaintext-buffer
    /// recycler. See <see cref="RestoreFileAsync"/> for the contract.</param>
    private async Task<string> RestoreWithBoundedParallelDownloadsAsync(
        List<ChunkInfo> sortedChunks,
        BackedUpFile file,
        string tempPath,
        MemoryBudget memoryBudget,
        IProgress<(long current, long total)>? progress,
        Action<long> updateCurrentBytes,
        FileOperationDiagnostics diag,
        BandwidthScheduler? bandwidthScheduler,
        ChunkBufferPool? largeChunkPool,
        ChunkBufferPool? smallChunkPool,
        CancellationToken cancellationToken)
    {
        var maxChunkBytes = sortedChunks.Max(c => c.Length);
        var totalChunkBytes = sortedChunks.Sum(c => (long)c.Length);
        var gcMemBefore = GC.GetTotalMemory(false);
        var fileName = Path.GetFileName(file.LocalPath);

        // Adaptive concurrency: scale inversely with chunk size to cap memory usage
        var effectiveConcurrency = ComputeAdaptiveChunkConcurrency(maxChunkBytes);
        Log($"BoundedParallelDownload: Starting for '{fileName}' - " +
            $"{sortedChunks.Count} chunks, adaptive concurrency={effectiveConcurrency} " +
            $"(default={DefaultParallelChunkDownloads}, maxChunk={maxChunkBytes:N0}), " +
            $"totalChunkBytes={totalChunkBytes:N0}, GC.TotalMemory={gcMemBefore:N0} bytes");

        // Log if memory pressure could be dangerous:
        // Each parallel chunk needs ~2x memory during download (encrypted + plaintext buffers
        // overlap briefly during DecryptInto; encrypted buffer is returned before channel write)
        var estimatedPeakMemory = (long)maxChunkBytes * 2 * effectiveConcurrency;
        if (estimatedPeakMemory > 1_000_000_000) // >1 GB estimated peak
        {
            Log($"BoundedParallelDownload: WARNING '{fileName}' - Estimated peak memory={estimatedPeakMemory:N0} bytes " +
                $"({effectiveConcurrency} parallel x {maxChunkBytes:N0} bytes x 2 copies). " +
                $"Risk of OutOfMemoryException for large chunks.");
        }

        // B62 (C): channel capacity equals the in-order window so a producer
        // cannot dispatch more than `effectiveConcurrency` chunks ahead of
        // the writer. Combined with the writer-released `windowSlots`
        // semaphore below, this caps reorder-buffer depth at exactly the
        // configured concurrency and removes the pre-B62 4× headroom that
        // let buffered higher-index chunks hold plaintext budget while the
        // next-needed chunk parked in `AcquireAsync`.
        var channelCapacity = effectiveConcurrency;
        var channelOptions = new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        var channel = Channel.CreateBounded<(int index, byte[] data, int length, ChunkBufferPool? pool)>(channelOptions);

        // B62 (C): writer-released in-order chunk window. The producer
        // acquires a slot BEFORE asking for budget; the writer releases the
        // slot AFTER each successful chunk write (in any order). This caps
        // outstanding-but-not-yet-written chunks at `effectiveConcurrency`
        // and structurally prevents the deadlock where buffered higher-index
        // chunks hold budget that the next-needed chunk needs to acquire.
        // The semaphore is the single point that gates "how far ahead of
        // the writer is the producer allowed to go"; budget alone cannot
        // express that ordering constraint.
        using var windowSlots = new SemaphoreSlim(effectiveConcurrency, effectiveConcurrency);
        
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? downloadException = null;
        long currentBytes = 0;
        int chunksWritten = 0;
        int chunksDownloaded = 0;
        // B62: hoisted from the writer lambda so the stall watchdog can sample it.
        // Mutated only by the writer (SingleReader channel); watchdog reads via Volatile.
        int nextChunkToWrite = 0;

        // Pipeline metrics counters (updated by Interlocked from producer/consumer tasks)
        int metricRetries = 0;
        int metricReorderMax = 0;
        var fileStopwatch = Stopwatch.StartNew();
        // Snapshot the budget stall count at the start so we can compute per-file stalls
        var stallCountBaseline = memoryBudget.StallCount;

        // B62 stall watchdog state. The bounded-parallel pipeline can deadlock when a
        // shared MemoryBudget is over-subscribed across many parallel files: every
        // file's writer holds plaintext-half budget for buffered out-of-order chunks,
        // which prevents the next-needed chunk from acquiring budget, which prevents
        // the writer from advancing, which prevents budget from being released. The
        // watchdog periodically samples pipeline state; if nothing has progressed for
        // a long time it logs a full snapshot (writer position, buffered indices,
        // in-flight acquire waits, budget usage, waiter count). This is the only way
        // to see the deadlock from outside, since by definition no exception fires.
        long lastWriterProgressTicks = Environment.TickCount64;
        long lastDownloadCompletedTicks = Environment.TickCount64;
        int acquireWaitingCount = 0;
        int snapshotsLogged = 0;
        // B63: pendingChunks lives in the outer scope so the watchdog can sample it
        // under `pendingChunksLock`. Pre-B63 the writer maintained a parallel
        // ConcurrentDictionary just for the snapshot, which doubled per-chunk
        // bookkeeping cost on the hot path and could drift from `pendingChunks`
        // if the writer ever forgot to mirror an add/remove. The locked snapshot
        // is taken at most once per WatchdogPollMs (5 s) so contention is
        // negligible.
        var pendingChunks = new Dictionary<int, (byte[] data, int length, ChunkBufferPool? pool)>();
        var pendingChunksLock = new Lock();

        // Producer task: Download chunks in parallel and write to channel
        var producerTask = Task.Run(async () =>
        {
            // B62 (C): the in-flight semaphore is REPLACED by the writer-
            // released `windowSlots` above. The pre-B62 producer-released
            // `semaphore` allowed `effectiveConcurrency` chunks to be
            // *downloading* concurrently, but did not bound how far ahead
            // of the writer the producer could get -- a fast producer could
            // run all chunks ahead of a slow writer, exhausting budget. The
            // `windowSlots` acquire below moves the gate to "outstanding-but-
            // not-yet-written" which is the actual quantity that needs to
            // be bounded for the deadlock to be impossible.
            var downloadTasks = new List<Task>();
            try
            {
                Log("BoundedParallelDownload.Producer: Starting download tasks");

                foreach (var chunk in sortedChunks)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    // B62 (C): block here until the writer has caught up
                    // enough that one more chunk can be dispatched without
                    // exceeding the in-order window. This is the single
                    // structural change that makes the reorder-buffer
                    // deadlock impossible -- the producer cannot dispatch
                    // more than `effectiveConcurrency` chunks ahead of the
                    // writer's current position, so the writer's next-
                    // needed chunk can never be locked out by buffered
                    // higher-index chunks holding budget.
                    await windowSlots.WaitAsync(linkedCts.Token);

                    var downloadTask = Task.Run(async () =>
                    {
                        // B71 (W5 Phase 3 Commit 3): pick the restore-scope
                        // recycler whose bucket geometry covers this chunk's
                        // length. The producer/consumer-side return MUST go
                        // through the same pool reference so a buffer rented
                        // from the large pool cannot be returned to the small
                        // pool (or vice versa); pinning `selectedPool` to the
                        // download lets the writer dispatch correctly via the
                        // per-chunk tuple field even after the producer has
                        // exited. When neither pool is supplied the path falls
                        // through to ArrayPool<byte>.Shared exactly as pre-B71.
                        var selectedPool = chunk.Length >= ChunkingService.PoolSkipThresholdBytes
                            ? largeChunkPool
                            : smallChunkPool;
                        // B73 (W5 Phase 4 Commit 2): mirror the plaintext pool
                        // selection for the encrypted scratch buffer that
                        // AzureBlobService.DownloadChunkStreamingAsync rents.
                        // Partition is on encrypted size (plaintext + EncryptionOverhead)
                        // so a plaintext chunk that lands exactly on the 16 MB
                        // boundary is routed to the large pool because its
                        // encrypted form (+37 bytes) clears the threshold. The
                        // encrypted buffer's lifetime is strictly internal to
                        // the download call so the source-pool reference does
                        // NOT need to be pinned on the per-chunk channel tuple
                        // the way the plaintext source does in B71.
                        //
                        // B74 (W5 Phase 4 Commit 3, Fix C2): same large-pool
                        // revert as BackupOrchestrator. Restore the
                        // pre-B73 ArrayPool routing for large encrypted
                        // download buffers so gen-2 trim continues to
                        // decay the steady-state residency on large-chunk
                        // restore workloads. The small-pool routing is
                        // preserved (bounded cap + B72 retention charge).
                        var selectedEncryptedPool = (chunk.Length + EncryptionService.EncryptionOverhead) >= ChunkingService.PoolSkipThresholdBytes
                            ? null
                            : smallChunkPool;
                        // Two-phase memory budget for accurate memory modeling:
                        // Phase A: Acquire 2× chunk size before download (encrypted + plaintext overlap during DecryptInto)
                        // Phase B: Release 1× after download returns (encrypted buffer freed inside blob service)
                        // Phase C: Consumer releases remaining 1× after writing plaintext to disk
                        var chunkMemoryCost = (long)chunk.Length * 2;
                        // B63: track precisely how much budget we still owe so the
                        // catch branches do not over-release. Pre-B63 the OCE/general
                        // catches always called Release(chunkMemoryCost), but if the
                        // exception fired AFTER Phase B (line `memoryBudget.Release(chunk.Length)`)
                        // and BEFORE the consumer received the chunk, we would have
                        // released chunkLength twice. After the channel write succeeds
                        // ownership transfers to the writer and we owe nothing.
                        long owedBudget = 0;
                        // B63: the buffer returned by DownloadChunkStreamingAsync is
                        // ArrayPool-rented. The catch branches must return it when the
                        // chunk never reaches the consumer; pre-B63 they did not, leaking
                        // pooled buffers on every cancelled or failed download that had
                        // already produced a chunk.
                        byte[]? rentedBuffer = null;
                        Interlocked.Increment(ref acquireWaitingCount);
                        try
                        {
                            await memoryBudget.AcquireAsync(chunkMemoryCost, linkedCts.Token);
                            owedBudget = chunkMemoryCost;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref acquireWaitingCount);
                        }
                        try
                        {
                            linkedCts.Token.ThrowIfCancellationRequested();

                            // Download with retry for transient Azure failures (503, timeouts).
                            // At 192 concurrent downloads, transient errors are expected.
                            byte[]? chunkBuffer = null;
                            int chunkLength = 0;
                            for (var attempt = 0; attempt <= MaxChunkRetries; attempt++)
                            {
                                try
                                {
                                    (chunkBuffer, chunkLength) = await _blobService.DownloadChunkStreamingAsync(chunk.BlobName, selectedPool, selectedEncryptedPool, linkedCts.Token);
                                    break; // success
                                }
                                catch (Exception ex) when (attempt < MaxChunkRetries && IsTransientError(ex))
                                {
                                    Interlocked.Increment(ref metricRetries);
                                    // B62 (B): a transient HTTP error means the server is
                                    // pushing back. Notify the AIMD scheduler so the next
                                    // dispatch wave halves file-level concurrency rather
                                    // than waiting for an EWMA throughput dip to register.
                                    bandwidthScheduler?.NotifyTransientError();
                                    var delay = ChunkRetryBaseDelayMs * (1 << attempt); // exponential backoff
                                    diag.RecordChunk("TransientRetry", chunk.Index, chunk.Hash, chunk.Length,
                                        extra: $"attempt={attempt + 1}/{MaxChunkRetries + 1}, error={ex.GetType().Name}");
                                    Log($"BoundedParallelDownload.Producer: '{fileName}' transient error on chunk {chunk.Index} " +
                                        $"(attempt {attempt + 1}/{MaxChunkRetries + 1}): {ex.GetType().Name}: {ex.Message}, " +
                                        $"retrying in {delay}ms");
                                    await Task.Delay(delay, linkedCts.Token);
                                }
                            }

                            rentedBuffer = chunkBuffer;

                            // B63: stamp "download completed" BEFORE the bounded-channel
                            // WriteAsync. Pre-B63 this stamp was set after WriteAsync, so a
                            // slow writer (channel full) made the download stage look idle
                            // even though every download was succeeding back-to-back. The
                            // watchdog's "downloads idle" line is supposed to discriminate
                            // a download-stage stall from a writer-stage stall; that only
                            // works if the timestamp tracks the download stage proper.
                            Volatile.Write(ref lastDownloadCompletedTicks, Environment.TickCount64);

                            diag.RecordChunk("Downloaded", chunk.Index, chunk.Hash,
                                chunkLength, extra: $"blob={chunk.BlobName}, expectedLen={chunk.Length}");
                            VerifyChunkIntegrity(chunkBuffer.AsSpan(0, chunkLength), chunk, file.LocalPath);
                            diag.RecordChunk("Verified", chunk.Index, chunk.Hash,
                                chunkLength, extra: "hash+size OK");

                            // Phase B: Encrypted buffer was returned inside DownloadChunkStreamingAsync.
                            // Release that portion now — only the plaintext buffer remains in the channel.
                            memoryBudget.Release(chunk.Length);
                            owedBudget -= chunk.Length;

                            await channel.Writer.WriteAsync((chunk.Index, chunkBuffer!, chunkLength, selectedPool), linkedCts.Token);
                            // B63: ownership of buffer + remaining plaintext budget has
                            // now transferred to the writer. We owe nothing.
                            owedBudget = 0;
                            rentedBuffer = null;

                            var count = Interlocked.Increment(ref chunksDownloaded);
                            if (count % 50 == 0 || count == sortedChunks.Count)
                            {
                                Log($"BoundedParallelDownload.Producer: '{fileName}' downloaded {count}/{sortedChunks.Count} chunks, " +
                                    $"lastChunkSize={chunkLength:N0}, GC.TotalMemory={GC.GetTotalMemory(false):N0}");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // B63: only release what we still owe (see `owedBudget` above)
                            // and only return the buffer if we still hold it. Pre-B63
                            // these branches blindly released chunkMemoryCost and never
                            // returned the rented buffer, which both over-released budget
                            // (when OCE fired between Phase B and channel write) and leaked
                            // ArrayPool buffers (when OCE fired after the buffer was rented).
                            if (owedBudget > 0) memoryBudget.Release(owedBudget);
                            if (rentedBuffer != null) ReturnPlaintextBuffer(rentedBuffer, selectedPool);
                            // B62 (C): chunk will never reach the writer, so the writer
                            // will never release this window slot. Release it here so the
                            // producer loop can dispatch the next chunk during shutdown.
                            windowSlots.Release();
                            throw;
                        }
                        catch (Exception ex)
                        {
                            if (owedBudget > 0) memoryBudget.Release(owedBudget);
                            if (rentedBuffer != null) ReturnPlaintextBuffer(rentedBuffer, selectedPool);
                            // B62 (C): same reasoning as the cancellation branch -- the
                            // chunk will never reach the writer because we are about to
                            // cancel the linked CTS, so the writer cannot release the slot.
                            windowSlots.Release();
                            var wasFirst = downloadException == null;
                            diag.RecordChunk("DownloadFailed", chunk.Index, chunk.Hash, chunk.Length,
                                extra: $"error={ex.GetType().Name}: {ex.Message}, firstError={wasFirst}");
                            Log($"BoundedParallelDownload.Producer: '{fileName}' EXCEPTION downloading chunk {chunk.Index} " +
                                $"(blob={chunk.BlobName}, chunkLength={chunk.Length:N0}): " +
                                $"{ex.GetType().Name}: {ex.Message} " +
                                $"[thread={Environment.CurrentManagedThreadId}, firstError={wasFirst}]");
                            Log($"BoundedParallelDownload.Producer: StackTrace: {ex.StackTrace}");
                            downloadException ??= ex;
                            await linkedCts.CancelAsync();
                            throw;
                        }
                        // B62 (C): no `finally { semaphore.Release(); }` block here --
                        // the writer-released `windowSlots` semaphore is released by
                        // `WriteChunkAndReleaseAsync` in the consumer instead. The two
                        // failure branches above release explicitly because their chunks
                        // never reach the writer.
                    }, linkedCts.Token);

                    downloadTasks.Add(downloadTask);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"BoundedParallelDownload.Producer: EXCEPTION in producer: {ex.GetType().Name}: {ex.Message}");
                downloadException ??= ex;
            }
            finally
            {
                // B62 (C): no semaphore disposal here -- `windowSlots` lives in the
                // outer `using` scope and is disposed only after both producer and
                // writer have fully drained, eliminating the pre-B62 race where a
                // cancelled download task could call `Release` against a disposed
                // semaphore.
                try
                {
                    if (downloadTasks.Count > 0)
                    {
                        await Task.WhenAll(downloadTasks);
                    }
                    Log("BoundedParallelDownload.Producer: All downloads completed");
                }
                catch (Exception ex)
                {
                    Log($"BoundedParallelDownload.Producer: Downloads finished with error: {ex.GetType().Name}: {ex.Message}");
                    downloadException ??= ex;
                }

                channel.Writer.Complete(downloadException);
                Log("BoundedParallelDownload.Producer: Channel writer completed");
            }
        }, linkedCts.Token);
        
        // Consumer task: Read chunks from channel, write to file in order,
        // and compute the file hash incrementally (avoids re-reading the entire file afterward)
        string? computedFileHash = null;
        var writerTask = Task.Run(async () =>
        {
            using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            Log($"BoundedParallelDownload.Consumer: Opening output file: {tempPath}");
            await using FileStream outputStream = new(tempPath, FileMode.Create, FileAccess.Write, 
                FileShare.None, bufferSize: LargeFileStreamBufferSize, useAsync: true);

            // Pre-allocate the file to its final size to reduce NTFS fragmentation
            // and avoid repeated metadata updates as the file grows
            if (file.FileSize > 0)
            {
                outputStream.SetLength(file.FileSize);
                outputStream.Seek(0, SeekOrigin.Begin);
            }

            // Throttle progress reports to avoid flooding the UI thread
            // (~200ms minimum between reports instead of per-chunk)
            var progressStopwatch = Stopwatch.StartNew();
            const long ProgressThrottleMs = 200;

            try
            {
                // Local function: writes one chunk to disk, updates hash + counters,
                // returns the plaintext buffer to its source (per-chunk pool when
                // present, else the shared ArrayPool), and releases the memory
                // budget. Shared by the direct-write and pending-drain paths.
                async Task WriteChunkAndReleaseAsync(byte[] chunkData, int chunkLength, ChunkBufferPool? sourcePool)
                {
                    // B63: every successful chunk write owes the window slot back
                    // to the producer, otherwise the producer can starve. Wrap the
                    // body in a try/finally so a transient FileStream.WriteAsync
                    // exception does not leak a slot and stall the next chunk.
                    // The pre-B63 code released the slot inline at the end of the
                    // body, so any throw before that line silently consumed a slot
                    // permanently.
                    bool windowSlotReleased = false;
                    try
                    {
                        await outputStream.WriteAsync(chunkData.AsMemory(0, chunkLength), linkedCts.Token);
                        incrementalHash.AppendData(chunkData.AsSpan(0, chunkLength));
                        currentBytes += chunkLength;
                        chunksWritten++;
                        nextChunkToWrite++;
                        ReturnPlaintextBuffer(chunkData, sourcePool);
                        // Phase C: Release remaining 1× for plaintext buffer
                        // (producer already released the encrypted buffer portion in Phase B)
                        memoryBudget.Release(chunkLength);
                        // B62 (C): release the writer-released window slot so the
                        // producer can dispatch one more chunk. The slot is gated on
                        // ACTUAL write progress, not download progress, so the
                        // outstanding-but-not-written chunk count cannot exceed the
                        // configured window.
                        windowSlots.Release();
                        windowSlotReleased = true;
                        // B62 (B): feed the AIMD scheduler with real per-chunk
                        // throughput. Recorded in the writer (not the producer) so
                        // bytes that downloaded but never landed do not skew the
                        // bandwidth signal upward.
                        bandwidthScheduler?.RecordBytesCompleted(chunkLength);
                        // B62 watchdog: writer made forward progress — reset stall timer.
                        Volatile.Write(ref lastWriterProgressTicks, Environment.TickCount64);
                    }
                    finally
                    {
                        // Belt-and-braces: if WriteAsync, hash, or budget release threw
                        // before the inline Release line, we still owe the slot to the
                        // producer. Without this, the producer parks in WaitAsync and the
                        // pipeline stalls until cancellation tears it down.
                        if (!windowSlotReleased) windowSlots.Release();
                    }
                }

                await foreach (var (index, data, length, pool) in channel.Reader.ReadAllAsync(linkedCts.Token))
                {
                    if (index == nextChunkToWrite)
                    {
                        await WriteChunkAndReleaseAsync(data, length, pool);

                        while (true)
                        {
                            (byte[] data, int length, ChunkBufferPool? pool) pending;
                            lock (pendingChunksLock)
                            {
                                if (!pendingChunks.TryGetValue(nextChunkToWrite, out pending))
                                    break;
                                pendingChunks.Remove(nextChunkToWrite);
                            }
                            await WriteChunkAndReleaseAsync(pending.data, pending.length, pending.pool);
                        }

                        // Throttled progress reporting to reduce UI thread pressure
                        if (progressStopwatch.ElapsedMilliseconds >= ProgressThrottleMs)
                        {
                            updateCurrentBytes(currentBytes);
                            progress?.Report((currentBytes, file.FileSize));
                            progressStopwatch.Restart();
                        }

                        if (chunksWritten % 50 == 0)
                        {
                            int bufferedCount;
                            lock (pendingChunksLock) bufferedCount = pendingChunks.Count;
                            diag.Record($"[WRITER] '{fileName}' written {chunksWritten}/{sortedChunks.Count} chunks, " +
                                $"{currentBytes:N0}/{file.FileSize:N0} bytes, {bufferedCount} buffered");
                            Log($"BoundedParallelDownload.Consumer: '{fileName}' written {chunksWritten}/{sortedChunks.Count} chunks, {currentBytes} bytes, {bufferedCount} buffered");
                        }
                    }
                    else
                    {
                        int currentPending;
                        long bufferedBytes;
                        lock (pendingChunksLock)
                        {
                            pendingChunks[index] = (data, length, pool);
                            currentPending = pendingChunks.Count;
                            // Defer summing bytes until the verbose log path actually
                            // runs; the common path takes the lock for two pointer
                            // writes only.
                            bufferedBytes = 0;
                        }
                        // Track peak reorder buffer depth for metrics
                        int prevMax;
                        while (currentPending > (prevMax = Volatile.Read(ref metricReorderMax)))
                        {
                            Interlocked.CompareExchange(ref metricReorderMax, currentPending, prevMax);
                        }
                        if (currentPending % 10 == 0 || currentPending > effectiveConcurrency)
                        {
                            lock (pendingChunksLock)
                            {
                                bufferedBytes = pendingChunks.Values.Sum(d => (long)d.length);
                            }
                            Log($"BoundedParallelDownload.Consumer: '{fileName}' buffering chunk {index} (waiting for {nextChunkToWrite}), " +
                                $"{currentPending} chunks buffered ({bufferedBytes:N0} bytes), " +
                                $"GC.TotalMemory={GC.GetTotalMemory(false):N0}");
                        }
                    }
                }

                // Final progress report to ensure 100% is reported
                updateCurrentBytes(currentBytes);
                progress?.Report((currentBytes, file.FileSize));

                await outputStream.FlushAsync(linkedCts.Token);
                computedFileHash = Convert.ToHexString(incrementalHash.GetHashAndReset());
                diag.Record($"[WRITER] Complete: {chunksWritten}/{sortedChunks.Count} chunks, " +
                    $"{currentBytes:N0}/{file.FileSize:N0} bytes, hash={computedFileHash[..12]}...");
                Log($"BoundedParallelDownload.Consumer: '{fileName}' flushed output, {chunksWritten} chunks written, {currentBytes} bytes, hash={computedFileHash[..8]}...");
            }
            catch (ChannelClosedException)
            {
                int pendingCount;
                lock (pendingChunksLock) pendingCount = pendingChunks.Count;
                Log($"BoundedParallelDownload.Consumer: '{fileName}' channel closed unexpectedly. " +
                    $"chunksWritten={chunksWritten}, nextExpected={nextChunkToWrite}, " +
                    $"pendingBuffered={pendingCount}, " +
                    $"downloadException={downloadException?.GetType().Name ?? "none"}: {downloadException?.Message ?? "none"}");
                if (downloadException != null)
                    throw downloadException;
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                int pendingCount;
                lock (pendingChunksLock) pendingCount = pendingChunks.Count;
                Log($"BoundedParallelDownload.Consumer: '{fileName}' EXCEPTION writing: {ex.GetType().Name}: {ex.Message}");
                Log($"BoundedParallelDownload.Consumer: StackTrace: {ex.StackTrace}");
                Log($"BoundedParallelDownload.Consumer: State - chunksWritten={chunksWritten}, " +
                    $"nextExpected={nextChunkToWrite}, pendingBuffered={pendingCount}, " +
                    $"currentBytes={currentBytes:N0}, GC.TotalMemory={GC.GetTotalMemory(false):N0}");
                if (ex is IOException ioEx)
                {
                    Log($"BoundedParallelDownload.Consumer: IOException HResult=0x{ioEx.HResult:X8}, " +
                        $"tempPath={tempPath}");
                }
                throw;
            }
            finally
            {
                // Drain any chunks stuck in the reorder buffer on error/cancellation.
                // Without this, their ArrayPool buffers leak and their budget allocations
                // are never released — reducing available budget for subsequent files
                // in a shared multi-file restore.
                // These chunks already had Phase B release (encrypted portion) by the producer,
                // so only release the remaining 1× plaintext portion here.
                List<(byte[] data, int length, ChunkBufferPool? pool)> toDrain;
                lock (pendingChunksLock)
                {
                    if (pendingChunks.Count == 0)
                    {
                        toDrain = [];
                    }
                    else
                    {
                        toDrain = new List<(byte[] data, int length, ChunkBufferPool? pool)>(pendingChunks.Count);
                        foreach (var (_, pending) in pendingChunks) toDrain.Add(pending);
                        pendingChunks.Clear();
                    }
                }
                if (toDrain.Count > 0)
                {
                    Log($"BoundedParallelDownload.Consumer: Draining {toDrain.Count} pending chunks " +
                        $"(returning buffers and releasing budget)");
                    foreach (var pending in toDrain)
                    {
                        ReturnPlaintextBuffer(pending.data, pending.pool);
                        memoryBudget.Release(pending.length);
                        // B62 (C): each drained chunk holds a window slot the writer
                        // will never release on the success path, so release here so
                        // any in-flight producer that is parked in `windowSlots.WaitAsync`
                        // can unblock and observe the linked-CTS cancellation.
                        windowSlots.Release();
                    }
                }
            }
        }, linkedCts.Token);

        // B62 stall watchdog: runs alongside producer/writer and periodically samples
        // pipeline state. When the writer hasn't advanced for StallWarnSeconds we log
        // a structured snapshot showing exactly where the pipeline is parked. This is
        // the only way to observe a producer/writer/budget ordering deadlock from
        // outside, since by definition no exception fires and CPU/network drop to zero.
        // Lifetime is bounded by watchdogCts, which is cancelled in the finally below
        // before the watchdog Task is awaited.
        const int WatchdogPollMs = 5_000;
        const int StallWarnSeconds = 30;
        const int MaxSnapshotsLogged = 20;
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchdogTask = Task.Run(async () =>
        {
            try
            {
                while (!watchdogCts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(WatchdogPollMs, watchdogCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    var nowTicks = Environment.TickCount64;
                    var writerIdleSec = (nowTicks - Volatile.Read(ref lastWriterProgressTicks)) / 1000;
                    var downloadIdleSec = (nowTicks - Volatile.Read(ref lastDownloadCompletedTicks)) / 1000;

                    if (writerIdleSec < StallWarnSeconds)
                        continue;
                    if (Volatile.Read(ref snapshotsLogged) >= MaxSnapshotsLogged)
                        continue;

                    Interlocked.Increment(ref snapshotsLogged);

                    // Snapshot all the structural facts that determine whether this
                    // is a producer-side stall, a writer-side stall, or a budget
                    // ordering deadlock. The "smoking gun" combination is:
                    // waitersInAcquire > 0 AND budget nearly full AND nextChunkToWrite
                    // is itself the chunk parked in AcquireAsync.
                    var nextNeeded = Volatile.Read(ref nextChunkToWrite);
                    var written = Volatile.Read(ref chunksWritten);
                    var downloaded = Volatile.Read(ref chunksDownloaded);
                    var waitingAcquire = Volatile.Read(ref acquireWaitingCount);
                    int[] bufferedSnapshot;
                    int bufferedCount;
                    bool nextNeededIsBuffered;
                    // B63: snapshot pendingChunks under its lock instead of maintaining
                    // a parallel ConcurrentDictionary. Watchdog poll cadence is 5 s, so
                    // briefly blocking the writer for the snapshot has no measurable
                    // throughput impact and removes a whole class of "the parallel
                    // dictionary drifted from pendingChunks" bugs.
                    lock (pendingChunksLock)
                    {
                        bufferedCount = pendingChunks.Count;
                        bufferedSnapshot = pendingChunks.Keys.OrderBy(i => i).Take(16).ToArray();
                        nextNeededIsBuffered = pendingChunks.ContainsKey(nextNeeded);
                    }

                    var snapshot =
                        $"[STALL-WATCHDOG] '{fileName}' no writer progress for {writerIdleSec}s " +
                        $"(downloads idle for {downloadIdleSec}s). " +
                        $"chunks: total={sortedChunks.Count}, downloaded={downloaded}, written={written}, " +
                        $"nextNeeded={nextNeeded} (bufferedAlready={nextNeededIsBuffered}). " +
                        $"reorderBuffer={bufferedCount} chunks, firstFew=[{string.Join(",", bufferedSnapshot)}]. " +
                        $"acquireWaiting={waitingAcquire} download tasks parked in MemoryBudget.AcquireAsync. " +
                        $"budget: used={memoryBudget.UsedBytes:N0}/{memoryBudget.TotalBytes:N0} bytes " +
                        $"({(memoryBudget.IsUnlimited ? "unlimited" : (memoryBudget.UsedBytes * 100.0 / memoryBudget.TotalBytes).ToString("F1") + "%")}), " +
                        $"waiters={memoryBudget.WaitersCount}, stalls={memoryBudget.StallCount - stallCountBaseline}. " +
                        $"GC.TotalMemory={GC.GetTotalMemory(false):N0}.";

                    Log(snapshot);
                    diag.Record(snapshot);

                    // Smoking-gun classification. When the next-needed chunk is parked
                    // in AcquireAsync while the writer holds plaintext budget for a
                    // pile of higher-index chunks, that is the producer/writer/budget
                    // ordering deadlock. Call it out so the cause is unambiguous.
                    if (waitingAcquire > 0 && bufferedCount > 0 && !nextNeededIsBuffered &&
                        !memoryBudget.IsUnlimited &&
                        memoryBudget.UsedBytes * 10 >= memoryBudget.TotalBytes * 9)
                    {
                        var diagLine =
                            $"[STALL-WATCHDOG] '{fileName}' DEADLOCK SUSPECTED: " +
                            $"writer waiting for chunk {nextNeeded}, but {bufferedCount} higher-index " +
                            $"chunks are buffered (holding plaintext budget) and {waitingAcquire} " +
                            $"download(s) are blocked acquiring budget. Budget is " +
                            $"{memoryBudget.UsedBytes * 100.0 / memoryBudget.TotalBytes:F1}% used. " +
                            $"This is the shared-budget ordering deadlock.";
                        Log(diagLine);
                        diag.Record(diagLine);
                        // B62 (B): tell the AIMD scheduler that we observed evidence
                        // of contention. The new in-order window (B62 C) makes this
                        // path unreachable in practice, but if it ever fires we want
                        // the next dispatch wave to halve concurrency immediately
                        // rather than wait for a throughput-EWMA dip.
                        bandwidthScheduler?.NotifyStallObserved();
                    }
                }
            }
            catch (Exception ex)
            {
                // Watchdog must never surface — its only contract is to log.
                Log($"[STALL-WATCHDOG] '{fileName}' watchdog exited with {ex.GetType().Name}: {ex.Message}");
            }
        }, watchdogCts.Token);

        try
        {
            await Task.WhenAll(producerTask, writerTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log("BoundedParallelDownload: Cancelled by user");
            throw;
        }
        catch (Exception ex)
        {
            Log($"BoundedParallelDownload: '{fileName}' EXCEPTION in WhenAll: {ex.GetType().Name}: {ex.Message}");
            Log($"BoundedParallelDownload: WhenAll StackTrace: {ex.StackTrace}");
            
            // Unpack AggregateException to log ALL inner exceptions (Task.WhenAll only surfaces the first)
            if (ex is AggregateException agg)
            {
                Log($"BoundedParallelDownload: '{fileName}' AggregateException contains {agg.InnerExceptions.Count} inner exception(s):");
                for (int i = 0; i < agg.InnerExceptions.Count; i++)
                {
                    var inner = agg.InnerExceptions[i];
                    Log($"BoundedParallelDownload:   [{i}] {inner.GetType().Name}: {inner.Message}");
                    Log($"BoundedParallelDownload:   [{i}] StackTrace: {inner.StackTrace}");
                }
            }
            
            // Log producer vs consumer task states for diagnosis
            Log($"BoundedParallelDownload: producerTask.Status={producerTask.Status}, writerTask.Status={writerTask.Status}");
            if (producerTask.Exception != null)
            {
                Log($"BoundedParallelDownload: Producer exception: {producerTask.Exception.GetType().Name}: {producerTask.Exception.Message}");
            }
            if (writerTask.Exception != null)
            {
                Log($"BoundedParallelDownload: Writer exception: {writerTask.Exception.GetType().Name}: {writerTask.Exception.Message}");
            }
            
            if (downloadException != null)
            {
                Log($"BoundedParallelDownload: Original download exception: {downloadException.GetType().Name}: {downloadException.Message}");
                Log($"BoundedParallelDownload: Original download StackTrace: {downloadException.StackTrace}");
                throw downloadException;
            }
            throw;
        }
        finally
        {
            // B62: shut down the stall watchdog before returning. Cancel first so the
            // delay loop bails immediately, then await so any in-flight Log call lands
            // before the caller's per-file metrics are recorded. Watchdog never throws,
            // so no try/catch needed around the await.
            await watchdogCts.CancelAsync();
            try { await watchdogTask; } catch { /* watchdog never throws meaningfully */ }
        }

        if (chunksWritten != sortedChunks.Count)
        {
            throw new DataIntegrityException(
                $"Write incomplete: wrote {chunksWritten} chunks, expected {sortedChunks.Count}",
                file.LocalPath);
        }
        
        Log($"BoundedParallelDownload: '{fileName}' completed, wrote {chunksWritten} chunks, {currentBytes} bytes, " +
            $"GC.TotalMemory={GC.GetTotalMemory(false):N0}");

        // Record per-file restore pipeline metrics
        fileStopwatch.Stop();
        var fileElapsed = fileStopwatch.Elapsed.TotalSeconds;
        Metrics?.RecordFile(new FileMetrics
        {
            Operation = "restore",
            Path = file.LocalPath,
            Bytes = file.FileSize,
            Chunks = sortedChunks.Count,
            ChunkMin = sortedChunks.Min(c => c.Length),
            ChunkMax = maxChunkBytes,
            ElapsedSeconds = fileElapsed,
            ThroughputMBps = fileElapsed > 0 ? file.FileSize / fileElapsed / (1024 * 1024) : 0,
            EffectiveConcurrency = effectiveConcurrency,
            BudgetStalls = (int)(memoryBudget.StallCount - stallCountBaseline),
            Retries = Volatile.Read(ref metricRetries),
            ReorderMax = Volatile.Read(ref metricReorderMax)
        });

        return computedFileHash!;
    }

}
