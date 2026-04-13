using System.Buffers;
using System.Collections.Concurrent;
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

    public event EventHandler<RestoreProgressEventArgs>? ProgressChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>
    /// Directory where per-file .diag logs are written on error.
    /// Set by the UI layer. When null, uses the system temp directory.
    /// </summary>
    public string? DiagnosticsDirectory { get; set; }

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
    public async Task<bool> RestoreFileAsync(
        BackedUpFile file,
        string? restorePath = null,
        bool overwriteExisting = false,
        IProgress<(long current, long total)>? progress = null,
        MemoryBudget? memoryBudget = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        var diag = new FileOperationDiagnostics(file.LocalPath, "Restore", DiagnosticsDirectory);
        using var _ = diag.SetAmbient();
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
                // Use the caller's shared budget when provided (multi-file restores),
                // otherwise create a per-file budget from config (single-file restore from UI).
                var ownsMemoryBudget = memoryBudget == null;
                if (ownsMemoryBudget)
                {
                    var restoreConfig = _databaseService.GetConfiguration();
                    memoryBudget = MemoryBudget.FromConfig(restoreConfig);
                }

                Log($"RestoreFileAsync: Using bounded parallel downloads (adaptive concurrency, " +
                    $"memoryBudget={(!memoryBudget.IsUnlimited ? $"{memoryBudget.TotalBytes / (1024 * 1024)} MB" : "unlimited")}, " +
                    $"shared={!ownsMemoryBudget})");
                try
                {
                // Use bounded producer-consumer pattern with channels
                // Hash is computed incrementally as chunks are written in order — no file re-read needed
                        restoredHash = await RestoreWithBoundedParallelDownloadsAsync(
                            sortedChunks, file, tempPath, memoryBudget, progress, 
                            p => currentBytes = p, diag, cancellationToken);
                    }
                    finally
                    {
                        if (ownsMemoryBudget)
                            memoryBudget.Dispose();
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

                    ProgressChanged?.Invoke(this, new RestoreProgressEventArgs
                    {
                        FilePath = targetPath,
                        BytesRestored = currentBytes,
                        TotalBytes = file.FileSize,
                        ChunksRestored = sortedChunks.IndexOf(chunk) + 1,
                        TotalChunks = sortedChunks.Count
                    });
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
                try { File.Delete(tempPath); } catch { /* ignore cleanup errors */ }

                throw new DataIntegrityException(
                    $"File hash mismatch after restore: expected {file.FileHash}, got {restoredHash}",
                    file.LocalPath);
            }
            
            // Move temp file to final destination (atomic on same filesystem)
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
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore cleanup errors */ }
            throw; // Re-throw integrity exceptions
        }
        catch (OperationCanceledException)
        {
            Log("RestoreFileAsync: Operation cancelled");
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
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
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }

            ErrorOccurred?.Invoke(this, $"Failed to restore {file.LocalPath}: {ex.Message}");
            return false;
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
    private async Task<string> RestoreWithBoundedParallelDownloadsAsync(
        List<ChunkInfo> sortedChunks,
        BackedUpFile file,
        string tempPath,
        MemoryBudget memoryBudget,
        IProgress<(long current, long total)>? progress,
        Action<long> updateCurrentBytes,
        FileOperationDiagnostics diag,
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

        // Channel capacity is 4× download parallelism to decouple network from disk I/O.
        // When the disk writer pauses briefly (NTFS journal flush, antivirus scan), the
        // producers can continue downloading into the channel buffer instead of stalling.
        // The MemoryBudget prevents OOM regardless of channel size — chunks in the channel
        // hold their budget allocation until the consumer writes and releases them.
        var channelCapacity = effectiveConcurrency * 4;
        var channelOptions = new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        var channel = Channel.CreateBounded<(int index, byte[] data, int length)>(channelOptions);
        
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? downloadException = null;
        long currentBytes = 0;
        int chunksWritten = 0;
        int chunksDownloaded = 0;
        
        // Producer task: Download chunks in parallel and write to channel
        var producerTask = Task.Run(async () =>
        {
            var semaphore = new SemaphoreSlim(effectiveConcurrency);
            var downloadTasks = new List<Task>();
            try
            {
                Log("BoundedParallelDownload.Producer: Starting download tasks");

                foreach (var chunk in sortedChunks)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    await semaphore.WaitAsync(linkedCts.Token);

                    var downloadTask = Task.Run(async () =>
                    {
                        // Two-phase memory budget for accurate memory modeling:
                        // Phase A: Acquire 2× chunk size before download (encrypted + plaintext overlap during DecryptInto)
                        // Phase B: Release 1× after download returns (encrypted buffer freed inside blob service)
                        // Phase C: Consumer releases remaining 1× after writing plaintext to disk
                        var chunkMemoryCost = (long)chunk.Length * 2;
                        await memoryBudget.AcquireAsync(chunkMemoryCost, linkedCts.Token);
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
                                    (chunkBuffer, chunkLength) = await _blobService.DownloadChunkStreamingAsync(chunk.BlobName, linkedCts.Token);
                                    break; // success
                                }
                                catch (Exception ex) when (attempt < MaxChunkRetries && IsTransientError(ex))
                                {
                                    var delay = ChunkRetryBaseDelayMs * (1 << attempt); // exponential backoff
                                    diag.RecordChunk("TransientRetry", chunk.Index, chunk.Hash, chunk.Length,
                                        extra: $"attempt={attempt + 1}/{MaxChunkRetries + 1}, error={ex.GetType().Name}");
                                    Log($"BoundedParallelDownload.Producer: '{fileName}' transient error on chunk {chunk.Index} " +
                                        $"(attempt {attempt + 1}/{MaxChunkRetries + 1}): {ex.GetType().Name}: {ex.Message}, " +
                                        $"retrying in {delay}ms");
                                    await Task.Delay(delay, linkedCts.Token);
                                }
                            }

                            diag.RecordChunk("Downloaded", chunk.Index, chunk.Hash,
                                chunkLength, extra: $"blob={chunk.BlobName}, expectedLen={chunk.Length}");
                            VerifyChunkIntegrity(chunkBuffer.AsSpan(0, chunkLength), chunk, file.LocalPath);
                            diag.RecordChunk("Verified", chunk.Index, chunk.Hash,
                                chunkLength, extra: "hash+size OK");

                            // Phase B: Encrypted buffer was returned inside DownloadChunkStreamingAsync.
                            // Release that portion now — only the plaintext buffer remains in the channel.
                            memoryBudget.Release(chunk.Length);

                            await channel.Writer.WriteAsync((chunk.Index, chunkBuffer!, chunkLength), linkedCts.Token);

                            var count = Interlocked.Increment(ref chunksDownloaded);
                            if (count % 50 == 0 || count == sortedChunks.Count)
                            {
                                Log($"BoundedParallelDownload.Producer: '{fileName}' downloaded {count}/{sortedChunks.Count} chunks, " +
                                    $"lastChunkSize={chunkLength:N0}, GC.TotalMemory={GC.GetTotalMemory(false):N0}");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Download failed before Phase B release — release the full 2× cost
                            memoryBudget.Release(chunkMemoryCost);
                            throw;
                        }
                        catch (Exception ex)
                        {
                            memoryBudget.Release(chunkMemoryCost);
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
                        finally
                        {
                            semaphore.Release();
                        }
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
                // Always await in-flight download tasks before disposing the semaphore.
                // Without this, cancelled tasks that call semaphore.Release() in their
                // finally block race against disposal, causing ObjectDisposedException.
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

                semaphore.Dispose();
                channel.Writer.Complete(downloadException);
                Log("BoundedParallelDownload.Producer: Channel writer completed");
            }
        }, linkedCts.Token);
        
        // Consumer task: Read chunks from channel, write to file in order,
        // and compute the file hash incrementally (avoids re-reading the entire file afterward)
        string? computedFileHash = null;
        var writerTask = Task.Run(async () =>
        {
            var pendingChunks = new Dictionary<int, (byte[] data, int length)>();
            int nextChunkToWrite = 0;
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
                // returns the ArrayPool buffer, and releases the memory budget.
                // Shared by the direct-write and pending-drain paths.
                async Task WriteChunkAndReleaseAsync(byte[] chunkData, int chunkLength)
                {
                    await outputStream.WriteAsync(chunkData.AsMemory(0, chunkLength), linkedCts.Token);
                    incrementalHash.AppendData(chunkData.AsSpan(0, chunkLength));
                    currentBytes += chunkLength;
                    chunksWritten++;
                    nextChunkToWrite++;
                    ArrayPool<byte>.Shared.Return(chunkData, clearArray: true);
                    // Phase C: Release remaining 1× for plaintext buffer
                    // (producer already released the encrypted buffer portion in Phase B)
                    memoryBudget.Release(chunkLength);
                }

                await foreach (var (index, data, length) in channel.Reader.ReadAllAsync(linkedCts.Token))
                {
                    if (index == nextChunkToWrite)
                    {
                        await WriteChunkAndReleaseAsync(data, length);

                        while (pendingChunks.TryGetValue(nextChunkToWrite, out var pending))
                        {
                            pendingChunks.Remove(nextChunkToWrite);
                            await WriteChunkAndReleaseAsync(pending.data, pending.length);
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
                            diag.Record($"[WRITER] '{fileName}' written {chunksWritten}/{sortedChunks.Count} chunks, " +
                                $"{currentBytes:N0}/{file.FileSize:N0} bytes, {pendingChunks.Count} buffered");
                            Log($"BoundedParallelDownload.Consumer: '{fileName}' written {chunksWritten}/{sortedChunks.Count} chunks, {currentBytes} bytes, {pendingChunks.Count} buffered");
                        }
                    }
                    else
                    {
                        pendingChunks[index] = (data, length);
                        if (pendingChunks.Count % 10 == 0 || pendingChunks.Count > effectiveConcurrency)
                        {
                            var bufferedBytes = pendingChunks.Values.Sum(d => (long)d.length);
                            Log($"BoundedParallelDownload.Consumer: '{fileName}' buffering chunk {index} (waiting for {nextChunkToWrite}), " +
                                $"{pendingChunks.Count} chunks buffered ({bufferedBytes:N0} bytes), " +
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
                Log($"BoundedParallelDownload.Consumer: '{fileName}' channel closed unexpectedly. " +
                    $"chunksWritten={chunksWritten}, nextExpected={nextChunkToWrite}, " +
                    $"pendingBuffered={pendingChunks.Count}, " +
                    $"downloadException={downloadException?.GetType().Name ?? "none"}: {downloadException?.Message ?? "none"}");
                if (downloadException != null)
                    throw downloadException;
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"BoundedParallelDownload.Consumer: '{fileName}' EXCEPTION writing: {ex.GetType().Name}: {ex.Message}");
                Log($"BoundedParallelDownload.Consumer: StackTrace: {ex.StackTrace}");
                Log($"BoundedParallelDownload.Consumer: State - chunksWritten={chunksWritten}, " +
                    $"nextExpected={nextChunkToWrite}, pendingBuffered={pendingChunks.Count}, " +
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
                if (pendingChunks.Count > 0)
                {
                    Log($"BoundedParallelDownload.Consumer: Draining {pendingChunks.Count} pending chunks " +
                        $"(returning buffers and releasing budget)");
                    foreach (var (_, pending) in pendingChunks)
                    {
                        ArrayPool<byte>.Shared.Return(pending.data, clearArray: true);
                        memoryBudget.Release(pending.length);
                    }
                    pendingChunks.Clear();
                }
            }
        }, linkedCts.Token);
        
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
        
        if (chunksWritten != sortedChunks.Count)
        {
            throw new DataIntegrityException(
                $"Write incomplete: wrote {chunksWritten} chunks, expected {sortedChunks.Count}",
                file.LocalPath);
        }
        
        Log($"BoundedParallelDownload: '{fileName}' completed, wrote {chunksWritten} chunks, {currentBytes} bytes, " +
            $"GC.TotalMemory={GC.GetTotalMemory(false):N0}");

        return computedFileHash!;
    }

}
