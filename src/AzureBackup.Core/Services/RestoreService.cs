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
public class RestoreService
{
    private readonly LocalDatabaseService _databaseService;
    private readonly IBlobStorageService _blobService;
    private readonly EncryptionService _encryptionService;
    
    // Parallel download settings - increased for better bandwidth utilization
    // Default concurrency for moderate chunk sizes; adaptive method below adjusts for extremes.
    // Memory bounded by: effectiveConcurrency * maxChunkSize * ~3 copies
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
        // Target ~256 MB of in-flight chunk data (each chunk needs ~3x: encrypted + decrypted + channel buffer)
        const long TargetInFlightBytes = 256L * 1024 * 1024;
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

        var metadataBlobs = await _blobService.ListMetadataBlobsAsync(cancellationToken);
        var total = metadataBlobs.Count;
        Log($"ListRestorableFilesAsync: Found {total} metadata blobs");
        
        if (total == 0)
        {
            StatusChanged?.Invoke(this, "No files found in Azure");
            return [];
        }

        StatusChanged?.Invoke(this, $"Loading metadata for {total} files...");
        progress?.Report((0, total));

        // Download metadata in parallel with bounded concurrency
        const int maxParallelDownloads = 32;
        ConcurrentBag<BackedUpFile> files = [];
        var completed = 0;

        await Parallel.ForEachAsync(
            metadataBlobs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelDownloads,
                CancellationToken = cancellationToken
            },
            async (blobName, ct) =>
            {
                var file = await _blobService.DownloadFileMetadataAsync(blobName, ct);
                if (file != null)
                {
                    files.Add(file);
                }

                var count = Interlocked.Increment(ref completed);
                if (count % 50 == 0 || count == total)
                {
                    progress?.Report((count, total));
                    StatusChanged?.Invoke(this, $"Loading file metadata... {count:N0}/{total:N0}");
                }
            });

        Log($"ListRestorableFilesAsync: Loaded {files.Count} file metadata entries");
        StatusChanged?.Invoke(this, $"Found {files.Count} files available for restore");
        return files.ToList();
    }

    /// <summary>
    /// Restores a single file to the specified location.
    /// Verifies integrity after restore by comparing file hash.
    /// Uses parallel chunk downloads for files with multiple chunks.
    /// </summary>
    public async Task<bool> RestoreFileAsync(
        BackedUpFile file,
        string? restorePath = null,
        bool overwriteExisting = false,
        IProgress<(long current, long total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        Log($"RestoreFileAsync: Starting restore of '{Path.GetFileName(file.LocalPath)}' ({file.FileSize} bytes, {file.Chunks.Count} chunks)");
        
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
                Log($"RestoreFileAsync: Using bounded parallel downloads (adaptive concurrency)");
                // Use bounded producer-consumer pattern with channels
                // Hash is computed incrementally as chunks are written in order — no file re-read needed
                restoredHash = await RestoreWithBoundedParallelDownloadsAsync(
                    sortedChunks, file, tempPath, progress, 
                    p => currentBytes = p, cancellationToken);
            }
            else
            {
                Log("RestoreFileAsync: Single chunk, downloading directly");
                // Single chunk - download, write, and compute hash in one pass
                using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using FileStream outputStream = new(tempPath, FileMode.Create, FileAccess.Write, 
                    FileShare.None, bufferSize: 81920, useAsync: true);

                foreach (var chunk in sortedChunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Log($"RestoreFileAsync: Downloading chunk {chunk.Index} ({chunk.Length} bytes) blob={chunk.BlobName}");

                    // Download with retry for transient Azure failures — same pattern as multi-chunk path.
                    // Single-chunk files (3,000 small files) are especially vulnerable to transient 503s
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
                            Log($"RestoreFileAsync: Transient error on single chunk {chunk.Index} " +
                                $"(attempt {attempt + 1}/{MaxChunkRetries + 1}): {ex.GetType().Name}: {ex.Message}, " +
                                $"retrying in {delay}ms");
                            await Task.Delay(delay, cancellationToken);
                        }
                    }

                    // Verify chunk size and hash match metadata
                    Log($"RestoreFileAsync: Verifying chunk {chunk.Index} integrity");
                    VerifyChunkIntegrity(chunkData!, chunk, file.LocalPath);

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
            StatusChanged?.Invoke(this, $"Verifying integrity: {Path.GetFileName(targetPath)}");

            if (!string.Equals(restoredHash, file.FileHash, StringComparison.Ordinal))
            {
                Log($"RestoreFileAsync: HASH MISMATCH! expected={file.FileHash}, got={restoredHash}");
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
            // Clean up temp file on error
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
            
            ErrorOccurred?.Invoke(this, $"Failed to restore {file.LocalPath}: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Verifies downloaded chunk data matches expected hash and size.
    /// </summary>
    private void VerifyChunkIntegrity(byte[] chunkData, ChunkInfo chunk, string filePath)
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
        IProgress<(long current, long total)>? progress,
        Action<long> updateCurrentBytes,
        CancellationToken cancellationToken)
    {
        var maxChunkBytes = sortedChunks.Max(c => c.Length);
        var totalChunkBytes = sortedChunks.Sum(c => (long)c.Length);
        var gcMemBefore = GC.GetTotalMemory(false);

        // Adaptive concurrency: scale inversely with chunk size to cap memory usage
        var effectiveConcurrency = ComputeAdaptiveChunkConcurrency(maxChunkBytes);
        Log($"BoundedParallelDownload: Starting for '{Path.GetFileName(file.LocalPath)}' - " +
            $"{sortedChunks.Count} chunks, adaptive concurrency={effectiveConcurrency} " +
            $"(default={DefaultParallelChunkDownloads}, maxChunk={maxChunkBytes:N0}), " +
            $"totalChunkBytes={totalChunkBytes:N0}, GC.TotalMemory={gcMemBefore:N0} bytes");

        // Log if memory pressure could be dangerous:
        // Each parallel chunk needs ~3x memory (stream buffer + encrypted array + decrypted array)
        var estimatedPeakMemory = (long)maxChunkBytes * 3 * effectiveConcurrency;
        if (estimatedPeakMemory > 1_000_000_000) // >1 GB estimated peak
        {
            Log($"BoundedParallelDownload: WARNING - Estimated peak memory={estimatedPeakMemory:N0} bytes " +
                $"({effectiveConcurrency} parallel x {maxChunkBytes:N0} bytes x 3 copies). " +
                $"Risk of OutOfMemoryException for large chunks.");
        }

        // Channel capacity is 2x download parallelism so disk writes don't stall network downloads.
        var channelCapacity = effectiveConcurrency * 2;
        var channelOptions = new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        var channel = Channel.CreateBounded<(int index, byte[] data)>(channelOptions);
        
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
                        try
                        {
                            linkedCts.Token.ThrowIfCancellationRequested();

                            // Download with retry for transient Azure failures (503, timeouts).
                            // At 192 concurrent downloads, transient errors are expected.
                            byte[]? chunkData = null;
                            for (var attempt = 0; attempt <= MaxChunkRetries; attempt++)
                            {
                                try
                                {
                                    chunkData = await _blobService.DownloadChunkStreamingAsync(chunk.BlobName, linkedCts.Token);
                                    break; // success
                                }
                                catch (Exception ex) when (attempt < MaxChunkRetries && IsTransientError(ex))
                                {
                                    var delay = ChunkRetryBaseDelayMs * (1 << attempt); // exponential backoff
                                    Log($"BoundedParallelDownload.Producer: Transient error on chunk {chunk.Index} " +
                                        $"(attempt {attempt + 1}/{MaxChunkRetries + 1}): {ex.GetType().Name}: {ex.Message}, " +
                                        $"retrying in {delay}ms");
                                    await Task.Delay(delay, linkedCts.Token);
                                }
                            }

                            VerifyChunkIntegrity(chunkData!, chunk, file.LocalPath);

                            await channel.Writer.WriteAsync((chunk.Index, chunkData), linkedCts.Token);

                            var count = Interlocked.Increment(ref chunksDownloaded);
                            if (count % 50 == 0 || count == sortedChunks.Count)
                            {
                                Log($"BoundedParallelDownload.Producer: Downloaded {count}/{sortedChunks.Count} chunks, " +
                                    $"lastChunkSize={chunkData.Length:N0}, GC.TotalMemory={GC.GetTotalMemory(false):N0}");
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            var wasFirst = downloadException == null;
                            Log($"BoundedParallelDownload.Producer: EXCEPTION downloading chunk {chunk.Index} " +
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
            var pendingChunks = new Dictionary<int, byte[]>();
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
                await foreach (var (index, data) in channel.Reader.ReadAllAsync(linkedCts.Token))
                {
                    if (index == nextChunkToWrite)
                    {
                        await outputStream.WriteAsync(data, linkedCts.Token);
                        incrementalHash.AppendData(data);
                        currentBytes += data.Length;
                        chunksWritten++;
                        nextChunkToWrite++;

                        while (pendingChunks.TryGetValue(nextChunkToWrite, out var pendingData))
                        {
                            pendingChunks.Remove(nextChunkToWrite);
                            await outputStream.WriteAsync(pendingData, linkedCts.Token);
                            incrementalHash.AppendData(pendingData);
                            currentBytes += pendingData.Length;
                            chunksWritten++;
                            nextChunkToWrite++;
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
                            Log($"BoundedParallelDownload.Consumer: Written {chunksWritten}/{sortedChunks.Count} chunks, {currentBytes} bytes, {pendingChunks.Count} buffered");
                        }
                    }
                    else
                    {
                        pendingChunks[index] = data;
                        if (pendingChunks.Count % 10 == 0 || pendingChunks.Count > effectiveConcurrency)
                        {
                            var bufferedBytes = pendingChunks.Values.Sum(d => (long)d.Length);
                            Log($"BoundedParallelDownload.Consumer: Buffering chunk {index} (waiting for {nextChunkToWrite}), " +
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
                Log($"BoundedParallelDownload.Consumer: Flushed output, {chunksWritten} chunks written, {currentBytes} bytes, hash={computedFileHash[..8]}...");
            }
            catch (ChannelClosedException)
            {
                Log($"BoundedParallelDownload.Consumer: Channel closed unexpectedly. " +
                    $"chunksWritten={chunksWritten}, nextExpected={nextChunkToWrite}, " +
                    $"pendingBuffered={pendingChunks.Count}, " +
                    $"downloadException={downloadException?.GetType().Name ?? "none"}: {downloadException?.Message ?? "none"}");
                if (downloadException != null)
                    throw downloadException;
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"BoundedParallelDownload.Consumer: EXCEPTION writing: {ex.GetType().Name}: {ex.Message}");
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
            Log($"BoundedParallelDownload: EXCEPTION in WhenAll: {ex.GetType().Name}: {ex.Message}");
            Log($"BoundedParallelDownload: WhenAll StackTrace: {ex.StackTrace}");
            
            // Unpack AggregateException to log ALL inner exceptions (Task.WhenAll only surfaces the first)
            if (ex is AggregateException agg)
            {
                Log($"BoundedParallelDownload: AggregateException contains {agg.InnerExceptions.Count} inner exception(s):");
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
        
        Log($"BoundedParallelDownload: Completed, wrote {chunksWritten} chunks, {currentBytes} bytes, " +
            $"GC.TotalMemory={GC.GetTotalMemory(false):N0}");

        return computedFileHash!;
    }

    /// <summary>
    /// Restores multiple files to a specified directory using parallel file processing.
    /// Delegates to <see cref="RestoreFilesWithRemappingAsync"/> for a single parallel implementation.
    /// </summary>
    public async Task<RestoreResult> RestoreFilesAsync(
        IEnumerable<BackedUpFile> files,
        string restoreDirectory,
        bool preserveFolderStructure = true,
        bool overwriteExisting = false,
        IProgress<(int current, int total, string file)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentException.ThrowIfNullOrWhiteSpace(restoreDirectory);

        var fileList = files.ToList();

        // Pre-compute relative paths (needs the full list for common root detection)
        var allPaths = fileList.Select(f => f.LocalPath).ToList();

        // Build (file, targetPath) pairs and delegate to the unified parallel implementation
        var filesWithPaths = fileList.Select(file =>
        {
            var targetPath = preserveFolderStructure
                ? Path.Combine(restoreDirectory, GetRelativePath(allPaths, file.LocalPath))
                : Path.Combine(restoreDirectory, Path.GetFileName(file.LocalPath));
            return (file, targetPath);
        }).ToList();

        Log($"RestoreFilesAsync: Delegating {fileList.Count} files to RestoreFilesWithRemappingAsync");

        return await RestoreFilesWithRemappingAsync(
            filesWithPaths, overwriteExisting, progress,
            fileByteProgress: null, cancellationToken);
    }

    /// <summary>
    /// Performs a full restore of all backed up files.
    /// </summary>
    public async Task<RestoreResult> RestoreAllAsync(
        string restoreDirectory,
        bool overwriteExisting = false,
        IProgress<(int current, int total, string file)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        StatusChanged?.Invoke(this, "Starting full restore...");

        var files = await ListRestorableFilesAsync(progress: null, cancellationToken);
        return await RestoreFilesAsync(files, restoreDirectory, 
            preserveFolderStructure: true, overwriteExisting, progress, cancellationToken);
    }

    /// <summary>
    /// Gets the relative path from a list of paths.
    /// </summary>
    private static string GetRelativePath(List<string> allPaths, string currentPath)
    {
        if (allPaths.Count <= 1)
            return Path.GetFileName(currentPath);

        var commonRoot = PathHelper.FindCommonRoot(allPaths);

        if (string.IsNullOrEmpty(commonRoot))
            return Path.GetFileName(currentPath);

        return Path.GetRelativePath(commonRoot, currentPath);
    }

    /// <summary>
    /// Searches for files matching a pattern in the backup.
    /// </summary>
    public async Task<List<BackedUpFile>> SearchFilesAsync(
        string searchPattern,
        CancellationToken cancellationToken = default)
    {
        var allFiles = await ListRestorableFilesAsync(progress: null, cancellationToken);
        
        var pattern = searchPattern.ToLowerInvariant();
        return allFiles.Where(f => 
            Path.GetFileName(f.LocalPath).ToLowerInvariant().Contains(pattern) ||
            f.LocalPath.ToLowerInvariant().Contains(pattern))
            .ToList();
    }

    /// <summary>
    /// Deletes a file and all its chunks from Azure storage.
    /// </summary>
    public async Task<bool> DeleteFileAsync(BackedUpFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        try
        {
            StatusChanged?.Invoke(this, $"Deleting: {Path.GetFileName(file.LocalPath)}");

            // Delete all chunks
            foreach (var chunk in file.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _blobService.DeleteBlobAsync(chunk.BlobName, cancellationToken);
            }

            // Delete metadata
            var metadataHash = _encryptionService.ComputeHmacHex(file.LocalPath);
            var metadataBlobName = $"metadata/{metadataHash}";
            await _blobService.DeleteBlobAsync(metadataBlobName, cancellationToken);

            StatusChanged?.Invoke(this, $"Deleted: {Path.GetFileName(file.LocalPath)}");
            return true;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, $"Failed to delete {file.LocalPath}: {ex.Message}");
            return false;
        }
    }

    // Concurrency for parallel blob deletion — DELETE is stateless and carries no payload,
    // so higher concurrency than upload/download is safe. 128 concurrent DELETEs produce
    // ~1,280 req/s at 100ms average — well under Azure's 20,000 req/s account limit.
    private const int MaxParallelBlobDeletes = 128;

    /// <summary>
    /// Deletes multiple files and their chunks from Azure storage in parallel.
    /// Chunks across all files are collected and deleted with high concurrency,
    /// then metadata blobs are deleted in a second parallel pass.
    /// </summary>
    /// <param name="files">Files to delete from Azure</param>
    /// <param name="progress">Reports (filesCompleted, totalFiles, currentFileName)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of files that were successfully deleted</returns>
    public async Task<List<BackedUpFile>> DeleteFilesAsync(
        IReadOnlyList<BackedUpFile> files,
        IProgress<(int filesCompleted, int totalFiles, string currentFileName)>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0) return [];

        StatusChanged?.Invoke(this, $"Deleting {files.Count} files from Azure...");

        // Phase 1: Collect all blob names to delete (chunks + metadata) in one pass.
        // This avoids per-file sequential chunk enumeration during the parallel delete phase.
        List<(string blobName, int fileIndex)> allBlobs = [];
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            foreach (var chunk in file.Chunks)
            {
                allBlobs.Add((chunk.BlobName, i));
            }

            var metadataHash = _encryptionService.ComputeHmacHex(file.LocalPath);
            allBlobs.Add(($"metadata/{metadataHash}", i));
        }

        StatusChanged?.Invoke(this, $"Deleting {allBlobs.Count} blobs across {files.Count} files...");

        // Phase 2: Delete all blobs with high concurrency.
        // Track which files had failures so we can report partial success.
        ConcurrentDictionary<int, bool> failedFileIndices = new();
        int blobsDeleted = 0;

        await Parallel.ForEachAsync(
            allBlobs,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelBlobDeletes,
                CancellationToken = cancellationToken
            },
            async (blob, ct) =>
            {
                try
                {
                    await _blobService.DeleteBlobAsync(blob.blobName, ct);

                    var completed = Interlocked.Increment(ref blobsDeleted);
                    // Report at file-granularity by estimating file completion from blob count
                    if (completed % 50 == 0 || completed == allBlobs.Count)
                    {
                        var estimatedFilesComplete = (int)((long)completed * files.Count / allBlobs.Count);
                        progress?.Report((estimatedFilesComplete, files.Count,
                            $"{completed}/{allBlobs.Count} blobs deleted"));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedFileIndices.TryAdd(blob.fileIndex, true);
                    Log($"DeleteFilesAsync: Failed to delete blob {blob.blobName}: {ex.Message}");
                }
            });

        // Build success list
        List<BackedUpFile> successfullyDeleted = [];
        for (var i = 0; i < files.Count; i++)
        {
            if (!failedFileIndices.ContainsKey(i))
            {
                successfullyDeleted.Add(files[i]);
            }
        }

        progress?.Report((files.Count, files.Count,
            $"Done — {successfullyDeleted.Count} succeeded, {failedFileIndices.Count} failed"));

        StatusChanged?.Invoke(this,
            $"Deleted {successfullyDeleted.Count}/{files.Count} files ({allBlobs.Count} blobs)");

        return successfullyDeleted;
    }

    /// <summary>
    /// Performs a mirror sync from Azure backup to a local directory.
    /// This will:
    /// 1. Restore files that are missing or outdated locally
    /// 2. Delete local files that don't exist in the backup
    /// 3. Skip files that are identical
    /// </summary>
    /// <param name="backupFiles">Files from Azure backup to sync</param>
    /// <param name="targetDirectory">Local directory to sync to</param>
    /// <param name="sourceBasePath">Original base path in backup (e.g., "J:\")</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="fileByteProgress">Reports per-file byte progress (bytesCompleted, fileSize, fileIndex) for active file rows</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<MirrorSyncResult> MirrorSyncToLocalAsync(
        IEnumerable<BackedUpFile> backupFiles,
        string targetDirectory,
        string sourceBasePath,
        IProgress<(int current, int total, string file, string action)>? progress = null,
        IProgress<(long bytesCompleted, long fileSize, int fileIndex)>? fileByteProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backupFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBasePath);
        
        Log($"MirrorSyncToLocalAsync: Starting mirror sync from '{sourceBasePath}' to '{targetDirectory}'");
        MirrorSyncResult result = new();
        var fileList = backupFiles.ToList();
        
        // Normalize paths
        sourceBasePath = Path.GetFullPath(sourceBasePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        targetDirectory = Path.GetFullPath(targetDirectory);
        
        if (!Directory.Exists(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        StatusChanged?.Invoke(this, $"Mirror sync: {fileList.Count} files from backup");

        // Build a set of expected files in target directory (thread-safe for parallel access)
        ConcurrentDictionary<string, byte> expectedLocalFiles = new(StringComparer.OrdinalIgnoreCase);

        // Phase 1: Restore/update files from backup (parallel)
        var totalOperations = fileList.Count;
        int completedOps = 0;
        object resultLock = new();

        Log($"MirrorSyncToLocalAsync: Starting parallel restore phase ({totalOperations} files, max {MaxParallelFileRestores} concurrent)");

        await Parallel.ForEachAsync(
            fileList.Select((file, index) => (file, index)),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelFileRestores,
                CancellationToken = cancellationToken
            },
            async (item, ct) =>
            {
                var (backupFile, fileIndex) = item;
                var currentOp = Interlocked.Increment(ref completedOps);

                try
                {
                    // Calculate target path by remapping base path
                    var relativePath = PathHelper.GetRelativePathFromBase(backupFile.LocalPath, sourceBasePath);
                    var targetPath = Path.Combine(targetDirectory, relativePath);
                    expectedLocalFiles.TryAdd(targetPath, 0);

                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    // Check if local file exists and is up to date
                    if (File.Exists(targetPath))
                    {
                        FileInfo localInfo = new(targetPath);

                        // Compare size and modification time
                        if (localInfo.Length == backupFile.FileSize && 
                            Math.Abs((localInfo.LastWriteTimeUtc - backupFile.LastModified).TotalSeconds) < 2)
                        {
                            // File appears unchanged - verify with hash for certainty
                            var localHash = await HashHelper.ComputeFileHashAsync(targetPath, ct);
                            if (string.Equals(localHash, backupFile.FileHash, StringComparison.Ordinal))
                            {
                                lock (resultLock) { result.FilesUnchanged++; }
                                progress?.Report((currentOp, totalOperations, Path.GetFileName(targetPath), "Unchanged"));
                                return;
                            }
                        }
                    }

                    // File is missing or outdated - restore it
                    progress?.Report((currentOp, totalOperations, Path.GetFileName(targetPath), "Restoring"));

                    // Create per-file byte progress reporter using the pre-computed index
                    var individualFileProgress = fileByteProgress != null
                        ? new Progress<(long current, long total)>(p =>
                            fileByteProgress.Report((p.current, backupFile.FileSize, fileIndex)))
                        : null;

                    var logPrefix = $"MirrorSyncToLocalAsync: [{currentOp}/{totalOperations}]";
                    var (outcome, recoveredPath, _) = await RestoreFileWithRecoveryAsync(
                        backupFile, targetPath, overwriteExisting: true, individualFileProgress, logPrefix, ct);

                    lock (resultLock)
                    {
                        switch (outcome)
                        {
                            case FileRestoreOutcome.Success:
                                result.FilesTransferred++;
                                result.BytesTransferred += backupFile.FileSize;
                                break;
                            case FileRestoreOutcome.CorruptedRecovery:
                                result.FilesCorruptedRecovered++;
                                result.BytesTransferred += backupFile.FileSize;
                                if (recoveredPath != null)
                                    result.CorruptedRecoveryPaths.Add(recoveredPath);
                                break;
                            case FileRestoreOutcome.Failed:
                                result.FilesErrored++;
                                result.Errors.Add($"Failed to restore: {backupFile.LocalPath}");
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (resultLock)
                    {
                        result.FilesErrored++;
                        result.Errors.Add($"Error restoring {backupFile.LocalPath}: {ex.Message}");
                    }
                    Log($"MirrorSyncToLocalAsync: Error restoring {backupFile.LocalPath}: {ex.Message}");
                }
            });

        // Phase 2: Delete local files that don't exist in backup (mirror mode)
        StatusChanged?.Invoke(this, "Scanning for files to delete...");

        try
        {
            // Normalize target directory for comparison
            var normalizedTargetDir = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var localFiles = Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories);

            foreach (var localFile in localFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Security: Verify file is actually within target directory (protection against symlink attacks)
                var normalizedLocalFile = Path.GetFullPath(localFile);
                if (!normalizedLocalFile.StartsWith(normalizedTargetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    Log($"MirrorSyncToLocalAsync: Skipping file outside target directory: {localFile}");
                    continue;
                }

                // Preserve corrupted recovery files — these were created by AttemptCorruptedRecoveryAsync
                // and should not be deleted by the mirror cleanup phase
                if (normalizedLocalFile.Contains($"{Path.DirectorySeparatorChar}__corrupted__{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!expectedLocalFiles.ContainsKey(localFile))
                {
                    try
                    {
                        progress?.Report((Volatile.Read(ref completedOps), totalOperations, Path.GetFileName(localFile), "Deleting"));
                        File.Delete(localFile);
                        result.FilesDeleted++;
                        Log($"MirrorSyncToLocalAsync: Deleted {localFile}");
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"Failed to delete {localFile}: {ex.Message}");
                    }
                }
            }

            // Clean up empty directories
            FileSystemHelper.CleanEmptyDirectories(targetDirectory);
        }
        catch (Exception ex)
        {
            Log($"MirrorSyncToLocalAsync: Error during delete phase: {ex.Message}");
        }

        var summaryParts = new List<string>
        {
            $"{result.FilesTransferred} restored",
            $"{result.FilesDeleted} deleted",
            $"{result.FilesUnchanged} unchanged"
        };
        if (result.FilesCorruptedRecovered > 0)
            summaryParts.Add($"{result.FilesCorruptedRecovered} recovered to __corrupted__");
        if (result.FilesErrored > 0)
            summaryParts.Add($"{result.FilesErrored} errors");

        var summaryText = $"Mirror sync complete: {string.Join(", ", summaryParts)}";
        StatusChanged?.Invoke(this, summaryText);
        Log($"MirrorSyncToLocalAsync: {summaryText}");

        return result;
    }

    /// <summary>
    /// Result of a single file restore attempt with corrupted recovery support.
    /// </summary>
    private enum FileRestoreOutcome
    {
        Success,
        Failed,
        CorruptedRecovery
    }

    /// <summary>
    /// Restores a single file with full exception handling and corrupted recovery.
    /// This is the shared logic used by all batch restore methods to ensure consistent behavior.
    /// </summary>
    /// <returns>Outcome, recovered path (if corrupted recovery), and unrecoverable chunk count.</returns>
    private async Task<(FileRestoreOutcome outcome, string? recoveredPath, int unrecoverableChunks)> RestoreFileWithRecoveryAsync(
        BackedUpFile file,
        string targetPath,
        bool overwriteExisting,
        IProgress<(long current, long total)>? fileProgress,
        string logPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var success = await RestoreFileAsync(file, targetPath, overwriteExisting,
                fileProgress, cancellationToken);

            if (success)
            {
                Log($"{logPrefix} OK");
                return (FileRestoreOutcome.Success, null, 0);
            }

            Log($"{logPrefix} FAILED (returned false)");
            return (FileRestoreOutcome.Failed, null, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DataIntegrityException ex)
        {
            Log($"{logPrefix} INTEGRITY FAILURE: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Integrity error restoring {file.LocalPath}: {ex.Message}");

            Log($"{logPrefix} Attempting corrupted recovery");
            StatusChanged?.Invoke(this, $"Attempting corrupted recovery: {Path.GetFileName(file.LocalPath)}");

            var recovery = await AttemptCorruptedRecoveryAsync(file, targetPath, cancellationToken);
            if (recovery.HasValue)
            {
                var (recoveredPath, unrecoverableChunks) = recovery.Value;
                var status = unrecoverableChunks > 0
                    ? $"Recovered with {unrecoverableChunks} zero-filled chunk(s)"
                    : "Recovered (CRC mismatch only, data intact)";
                Log($"{logPrefix} RECOVERED to {recoveredPath} — {status}");
                ErrorOccurred?.Invoke(this, $"Corrupted recovery: {file.LocalPath} → {recoveredPath} ({status})");
                return (FileRestoreOutcome.CorruptedRecovery, recoveredPath, unrecoverableChunks);
            }

            Log($"{logPrefix} RECOVERY FAILED");
            ErrorOccurred?.Invoke(this, $"Failed to restore {file.LocalPath}: integrity error and recovery failed");
            return (FileRestoreOutcome.Failed, null, 0);
        }
        catch (Exception ex)
        {
            Log($"{logPrefix} EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            ErrorOccurred?.Invoke(this, $"Failed to restore {file.LocalPath}: {ex.Message}");
            return (FileRestoreOutcome.Failed, null, 0);
        }
    }

    /// <summary>
    /// Applies a single file restore outcome to a RestoreResult.
    /// </summary>
    private static void ApplyRestoreOutcome(
        RestoreResult result,
        FileRestoreOutcome outcome,
        BackedUpFile file,
        string targetPath,
        string? recoveredPath,
        int unrecoverableChunks)
    {
        switch (outcome)
        {
            case FileRestoreOutcome.Success:
                result.SuccessfulFiles.Add(targetPath);
                result.TotalBytesRestored += file.FileSize;
                break;
            case FileRestoreOutcome.CorruptedRecovery:
                result.CorruptedRecoveryFiles.Add((file.LocalPath, recoveredPath!, unrecoverableChunks));
                result.TotalBytesRestored += file.FileSize;
                break;
            case FileRestoreOutcome.Failed:
                result.FailedFiles.Add(file.LocalPath);
                break;
        }
    }

    /// <summary>
    /// Builds a summary status message from a RestoreResult.
    /// </summary>
    private static string BuildRestoreStatusMessage(RestoreResult result)
    {
        var parts = new List<string> { $"{result.SuccessfulFiles.Count} succeeded" };
        if (result.CorruptedRecoveryFiles.Count > 0)
            parts.Add($"{result.CorruptedRecoveryFiles.Count} recovered to __corrupted__");
        if (result.FailedFiles.Count > 0)
            parts.Add($"{result.FailedFiles.Count} failed");
        return $"Restore complete: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Attempts to recover a file with corrupted chunks to a __corrupted__ subfolder.
    /// Downloads each chunk individually with best-effort decryption (skips CRC32 check).
    /// Chunks that are completely unrecoverable (AES-GCM tag mismatch) are zero-filled
    /// so the rest of the file remains usable (e.g., video files will play with brief glitches).
    /// </summary>
    /// <returns>
    /// (recoveredPath, unrecoverableChunkCount) if recovery produced a file, or null if recovery failed entirely.
    /// </returns>
    private async Task<(string recoveredPath, int unrecoverableChunks)?> AttemptCorruptedRecoveryAsync(
        BackedUpFile file,
        string originalTargetPath,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(originalTargetPath) ?? string.Empty;
        var corruptedDir = Path.Combine(dir, "__corrupted__");
        var corruptedPath = Path.Combine(corruptedDir, Path.GetFileName(originalTargetPath));

        Log($"AttemptCorruptedRecoveryAsync: Recovering '{Path.GetFileName(file.LocalPath)}' to {corruptedPath}");
        StatusChanged?.Invoke(this, $"Attempting corrupted recovery: {Path.GetFileName(file.LocalPath)}");

        try
        {
            Directory.CreateDirectory(corruptedDir);

            var sortedChunks = file.Chunks.OrderBy(c => c.Index).ToList();
            var unrecoverableChunks = 0;
            var recoveredChunks = 0;

            // Shared zero buffer for efficient zero-fill (max 1 MB, reused across chunks)
            var zeroBuffer = new byte[Math.Min(1024 * 1024, sortedChunks.Max(c => c.Length))];

            await using FileStream outputStream = new(corruptedPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 81920, useAsync: true);

            foreach (var chunk in sortedChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunkData = await _blobService.DownloadChunkBestEffortAsync(chunk.BlobName, cancellationToken);

                if (chunkData != null)
                {
                    await outputStream.WriteAsync(chunkData, cancellationToken);
                    recoveredChunks++;
                }
                else
                {
                    // Zero-fill in blocks using shared buffer to avoid large allocations
                    var remaining = chunk.Length;
                    while (remaining > 0)
                    {
                        var writeSize = Math.Min(remaining, zeroBuffer.Length);
                        await outputStream.WriteAsync(zeroBuffer.AsMemory(0, writeSize), cancellationToken);
                        remaining -= writeSize;
                    }
                    unrecoverableChunks++;
                    Log($"AttemptCorruptedRecoveryAsync: Chunk {chunk.Index} UNRECOVERABLE, zero-filled ({chunk.Length} bytes)");
                }

                // Early bailout: if no chunks have been recovered so far and we've tried several,
                // the data is likely entirely unrecoverable (e.g., wrong key scenario)
                var totalAttempted = recoveredChunks + unrecoverableChunks;
                if (recoveredChunks == 0 && totalAttempted >= 3)
                {
                    Log($"AttemptCorruptedRecoveryAsync: Aborting — first {totalAttempted} chunks all unrecoverable");
                    try { await outputStream.DisposeAsync(); } catch { /* closing stream */ }
                    try { File.Delete(corruptedPath); } catch { /* best effort */ }
                    return null;
                }
            }

            Log($"AttemptCorruptedRecoveryAsync: Recovery complete — " +
                $"{recoveredChunks}/{sortedChunks.Count} chunks recovered, " +
                $"saved to {corruptedPath}");

            return (corruptedPath, unrecoverableChunks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"AttemptCorruptedRecoveryAsync: Recovery failed entirely: {ex.Message}");

            // Clean up partial file
            try { File.Delete(corruptedPath); } catch { /* best effort */ }

            return null;
        }
    }

    /// <summary>
    /// Restores files with path remapping support using size-aware parallelism.
    /// Each file can have a custom target path.
    /// Small files (&lt;1 MB) are processed with high parallelism (latency-bound),
    /// while large files use moderate parallelism with deep chunk-level concurrency.
    /// </summary>
    /// <param name="filesWithPaths">Files with their target paths</param>
    /// <param name="overwriteExisting">Whether to overwrite existing files</param>
    /// <param name="progress">Reports file-level progress (current file index, total files, filename)</param>
    /// <param name="fileByteProgress">Reports byte-level progress for current file (bytes completed, file index)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<RestoreResult> RestoreFilesWithRemappingAsync(
        IEnumerable<(BackedUpFile file, string targetPath)> filesWithPaths,
        bool overwriteExisting = false,
        IProgress<(int current, int total, string file)>? progress = null,
        IProgress<(long bytesCompleted, long fileSize, int fileIndex)>? fileByteProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filesWithPaths);

        RestoreResult result = new();
        var fileList = filesWithPaths.ToList();
        var totalFiles = fileList.Count;
        int[] completedFiles = [0];
        object resultLock = new();

        Log($"RestoreFilesWithRemappingAsync: Restoring {totalFiles} files with path remapping (parallel)");
        StatusChanged?.Invoke(this, $"Starting restore of {totalFiles} files");

        // Size-aware parallelism: small files are latency-bound (high parallelism),
        // large files are bandwidth-bound (moderate parallelism with deep chunk concurrency)
        const long SmallFileThreshold = 100L * 1024 * 1024; // 100 MB
        const int MaxParallelSmallFiles = 32;

        var smallFiles = new List<(BackedUpFile file, string targetPath, int originalIndex)>();
        var largeFiles = new List<(BackedUpFile file, string targetPath, int originalIndex)>();

        for (var i = 0; i < fileList.Count; i++)
        {
            if (fileList[i].file.FileSize <= SmallFileThreshold)
                smallFiles.Add((fileList[i].file, fileList[i].targetPath, i));
            else
                largeFiles.Add((fileList[i].file, fileList[i].targetPath, i));
        }

        // Pre-sort large files by size descending so the biggest files start first.
        // This saturates network bandwidth early and avoids a long tail of large files at the end.
        largeFiles.Sort((a, b) => b.file.FileSize.CompareTo(a.file.FileSize));

        Log($"RestoreFilesWithRemappingAsync: {smallFiles.Count} small files (≤100 MB, max {MaxParallelSmallFiles} concurrent), " +
            $"{largeFiles.Count} large files (max {MaxParallelFileRestores} concurrent)");

        // Run small and large file restores concurrently — they use different resources:
        // small files are latency-bound (HTTP round-trips), large files are bandwidth-bound (chunk streaming).
        // Running them together avoids wasting network bandwidth while small files do metadata I/O.
        List<Task> restoreTasks = [];

        if (smallFiles.Count > 0)
        {
            StatusChanged?.Invoke(this, $"Restoring {smallFiles.Count} small files...");
            restoreTasks.Add(Parallel.ForEachAsync(
                smallFiles,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelSmallFiles,
                    CancellationToken = cancellationToken
                },
                async (item, ct) =>
                {
                    await RestoreOneFileWithRemappingAsync(
                        item.file, item.targetPath, item.originalIndex, totalFiles,
                        overwriteExisting, progress, fileByteProgress, result, resultLock,
                        completedFiles, ct);
                }));
        }

        if (largeFiles.Count > 0)
        {
            StatusChanged?.Invoke(this, $"Restoring {largeFiles.Count} large files...");
            restoreTasks.Add(Parallel.ForEachAsync(
                largeFiles,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelFileRestores,
                    CancellationToken = cancellationToken
                },
                async (item, ct) =>
                {
                    await RestoreOneFileWithRemappingAsync(
                        item.file, item.targetPath, item.originalIndex, totalFiles,
                        overwriteExisting, progress, fileByteProgress, result, resultLock,
                        completedFiles, ct);
                }));
        }

        await Task.WhenAll(restoreTasks);

        Log($"RestoreFilesWithRemappingAsync: Complete - {result.SuccessfulFiles.Count} succeeded, " +
            $"{result.CorruptedRecoveryFiles.Count} corrupted-recovered, {result.FailedFiles.Count} failed, " +
            $"{result.TotalBytesRestored} bytes");
        StatusChanged?.Invoke(this, BuildRestoreStatusMessage(result));
        return result;
    }

    /// <summary>
    /// Restores a single file within a parallel batch, with path validation and thread-safe result aggregation.
    /// Shared by both <see cref="RestoreFilesAsync"/> and <see cref="RestoreFilesWithRemappingAsync"/>.
    /// </summary>
    private async Task RestoreOneFileWithRemappingAsync(
        BackedUpFile file,
        string targetPath,
        int fileIndex,
        int totalFiles,
        bool overwriteExisting,
        IProgress<(int current, int total, string file)>? progress,
        IProgress<(long bytesCompleted, long fileSize, int fileIndex)>? fileByteProgress,
        RestoreResult result,
        object resultLock,
        int[] completedFiles,
        CancellationToken cancellationToken)
    {
        var done = Interlocked.Increment(ref completedFiles[0]);
        Log($"RestoreFilesWithRemappingAsync: [{done}/{totalFiles}] '{Path.GetFileName(file.LocalPath)}' -> '{targetPath}' ({file.FileSize} bytes, {file.Chunks.Count} chunks)");
        progress?.Report((done, totalFiles, file.LocalPath));

        // Security: Validate target path doesn't contain suspicious patterns
        var normalizedPath = Path.GetFullPath(targetPath);
        if (normalizedPath.Contains(".." + Path.DirectorySeparatorChar) || 
            normalizedPath.Contains(".." + Path.AltDirectorySeparatorChar))
        {
            Log($"RestoreFilesWithRemappingAsync: Skipping suspicious path: {targetPath}");
            lock (resultLock)
            {
                result.FailedFiles.Add(file.LocalPath);
            }
            ErrorOccurred?.Invoke(this, $"Invalid target path (contains path traversal): {targetPath}");
            return;
        }

        // Ensure target directory exists
        var targetDir = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
        {
            Log($"RestoreFilesWithRemappingAsync: Creating directory: {targetDir}");
            Directory.CreateDirectory(targetDir);
        }

        // Create byte-level progress reporter for this file
        var individualFileProgress = fileByteProgress != null
            ? new Progress<(long current, long total)>(p =>
                fileByteProgress.Report((p.current, file.FileSize, fileIndex)))
            : null;

        var logPrefix = $"RestoreFilesWithRemappingAsync: [{done}/{totalFiles}]";
        var (outcome, recoveredPath, unrecoverableChunks) = await RestoreFileWithRecoveryAsync(
            file, normalizedPath, overwriteExisting, individualFileProgress, logPrefix, cancellationToken);

        lock (resultLock)
        {
            ApplyRestoreOutcome(result, outcome, file, normalizedPath, recoveredPath, unrecoverableChunks);
        }
    }

    #region Preview Generation Methods

    /// <summary>
    /// Generates a preview of a mirror sync operation without making any changes.
    /// Uses parallelism for file comparison: metadata checks (File.Exists, size, timestamp)
    /// are nearly instant; hash verification uses <see cref="Environment.ProcessorCount"/>
    /// concurrency since SHA-256 is CPU-bound when files are in OS cache and I/O-bound otherwise —
    /// ProcessorCount adapts to both (more cores = more throughput for cached; more I/O slots for uncached).
    /// </summary>
    public async Task<OperationPreview> PreviewMirrorSyncAsync(
        IEnumerable<BackedUpFile> backupFiles,
        string targetDirectory,
        string sourceBasePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backupFiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBasePath);

        OperationPreview preview = new()
        {
            OperationType = OperationType.MirrorSync,
            OperationDescription = "Sync local folder to match Azure backup",
            SourceDescription = $"Azure Backup ({sourceBasePath})",
            TargetDescription = targetDirectory
        };

        var fileList = backupFiles.ToList();
        sourceBasePath = Path.GetFullPath(sourceBasePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        targetDirectory = Path.GetFullPath(targetDirectory);

        ConcurrentDictionary<string, byte> expectedLocalFiles = new(StringComparer.OrdinalIgnoreCase);

        // Thread-safe collectors — parallelism makes List<T>.Add unsafe
        ConcurrentBag<PreviewFileAction> toSkip = [];
        ConcurrentBag<PreviewFileAction> toCreate = [];
        ConcurrentBag<PreviewFileAction> toOverwrite = [];

        Log($"PreviewMirrorSyncAsync: Checking {fileList.Count} files with parallelism={LocalIoParallelism}");

        await Parallel.ForEachAsync(
            fileList,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = LocalIoParallelism,
                CancellationToken = cancellationToken
            },
            async (backupFile, ct) =>
            {
                var relativePath = PathHelper.GetRelativePathFromBase(backupFile.LocalPath, sourceBasePath);
                var targetPath = Path.Combine(targetDirectory, relativePath);
                expectedLocalFiles.TryAdd(targetPath, 0);

                if (File.Exists(targetPath))
                {
                    FileInfo localInfo = new(targetPath);

                    // Quick metadata check — avoids hashing when size or timestamp differ
                    if (localInfo.Length == backupFile.FileSize &&
                        Math.Abs((localInfo.LastWriteTimeUtc - backupFile.LastModified).TotalSeconds) < 2)
                    {
                        var localHash = await HashHelper.ComputeFileHashAsync(targetPath, ct);
                        if (string.Equals(localHash, backupFile.FileHash, StringComparison.Ordinal))
                        {
                            toSkip.Add(new PreviewFileAction
                            {
                                FilePath = backupFile.LocalPath,
                                FileSize = backupFile.FileSize,
                                LastModified = backupFile.LastModified,
                                TargetPath = targetPath,
                                Action = FileActionType.Skip,
                                Reason = "File is identical"
                            });
                            return;
                        }
                    }

                    // File exists but is different
                    toOverwrite.Add(new PreviewFileAction
                    {
                        FilePath = backupFile.LocalPath,
                        FileSize = backupFile.FileSize,
                        LastModified = backupFile.LastModified,
                        TargetPath = targetPath,
                        Action = FileActionType.Overwrite,
                        Reason = localInfo.Length != backupFile.FileSize
                            ? $"Size differs (local: {localInfo.Length}, backup: {backupFile.FileSize})"
                            : "Content differs"
                    });
                }
                else
                {
                    // New file
                    toCreate.Add(new PreviewFileAction
                    {
                        FilePath = backupFile.LocalPath,
                        FileSize = backupFile.FileSize,
                        LastModified = backupFile.LastModified,
                        TargetPath = targetPath,
                        Action = FileActionType.Create,
                        Reason = "File does not exist locally"
                    });
                }
            });

        // Transfer concurrent results to preview lists (sorted for deterministic UI order)
        preview.FilesToSkip.AddRange(toSkip.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase));
        preview.FilesToCreate.AddRange(toCreate.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase));
        preview.FilesToOverwrite.AddRange(toOverwrite.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase));

        // Check for local files that don't exist in backup (will be deleted)
        if (Directory.Exists(targetDirectory))
        {
            var localFiles = Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories);
            foreach (var localFile in localFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!expectedLocalFiles.ContainsKey(localFile))
                {
                    FileInfo fileInfo = new(localFile);
                    preview.FilesToDelete.Add(new PreviewFileAction
                    {
                        FilePath = localFile,
                        FileSize = fileInfo.Length,
                        LastModified = fileInfo.LastWriteTime,
                        Action = FileActionType.Delete,
                        Reason = "Not in backup"
                    });
                }
            }
        }

        Log($"PreviewMirrorSyncAsync: {toSkip.Count} skip, {toCreate.Count} create, " +
            $"{toOverwrite.Count} overwrite, {preview.FilesToDelete.Count} delete");

        return preview;
    }

    /// <summary>
    /// Generates a preview of deleting files from Azure storage.
    /// </summary>
    public OperationPreview PreviewDeleteFromAzure(IEnumerable<BackedUpFile> filesToDelete)
    {
        ArgumentNullException.ThrowIfNull(filesToDelete);

        var fileList = filesToDelete.ToList();
        OperationPreview preview = new()
        {
            OperationType = OperationType.DeleteFromAzure,
            OperationDescription = $"Permanently delete {fileList.Count} file(s) from Azure storage",
            SourceDescription = "Azure Blob Storage",
            TargetDescription = "N/A (files will be removed)"
        };

        foreach (var file in fileList)
        {
            preview.FilesToDelete.Add(new PreviewFileAction
            {
                FilePath = file.LocalPath,
                FileSize = file.FileSize,
                LastModified = file.LastModified,
                Action = FileActionType.Delete,
                Reason = "User requested deletion"
            });
        }

        return preview;
    }

    /// <summary>
    /// Generates a preview of restoring files with path remapping.
    /// </summary>
    public OperationPreview PreviewRestoreWithRemapping(
        IEnumerable<(BackedUpFile file, string targetPath)> filesWithPaths)
    {
        ArgumentNullException.ThrowIfNull(filesWithPaths);

        var fileList = filesWithPaths.ToList();
        OperationPreview preview = new()
        {
            OperationType = OperationType.Restore,
            OperationDescription = $"Restore {fileList.Count} file(s) from Azure backup",
            SourceDescription = "Azure Blob Storage",
            TargetDescription = "Local file system (with path remapping)"
        };

        foreach (var (file, targetPath) in fileList)
        {
            if (File.Exists(targetPath))
            {
                FileInfo localInfo = new(targetPath);
                preview.FilesToOverwrite.Add(new PreviewFileAction
                {
                    FilePath = file.LocalPath,
                    FileSize = file.FileSize,
                    LastModified = file.LastModified,
                    TargetPath = targetPath,
                    Action = FileActionType.Overwrite,
                    Reason = $"Existing file will be replaced (local size: {localInfo.Length})"
                });
            }
            else
            {
                preview.FilesToCreate.Add(new PreviewFileAction
                {
                    FilePath = file.LocalPath,
                    FileSize = file.FileSize,
                    LastModified = file.LastModified,
                    TargetPath = targetPath,
                    Action = FileActionType.Create,
                    Reason = "New file"
                });
            }
        }

        return preview;
    }

    #endregion
}

public class RestoreProgressEventArgs : EventArgs
{
    public string FilePath { get; set; } = string.Empty;
    public long BytesRestored { get; set; }
    public long TotalBytes { get; set; }
    public int ChunksRestored { get; set; }
    public int TotalChunks { get; set; }
    
    public double PercentComplete => TotalBytes > 0 ? (double)BytesRestored / TotalBytes * 100 : 0;
}

public class RestoreResult
{
    public List<string> SuccessfulFiles { get; set; } = [];
    public List<string> FailedFiles { get; set; } = [];

    /// <summary>
    /// Files that failed normal restore but were partially recovered to a __corrupted__ subfolder.
    /// Each entry is (originalPath, corruptedPath, unrecoverableChunkCount).
    /// </summary>
    public List<(string OriginalPath, string RecoveredPath, int UnrecoverableChunks)> CorruptedRecoveryFiles { get; set; } = [];

    public long TotalBytesRestored { get; set; }

    public int TotalFilesProcessed => SuccessfulFiles.Count + FailedFiles.Count + CorruptedRecoveryFiles.Count;
    public bool IsSuccess => FailedFiles.Count == 0 && CorruptedRecoveryFiles.Count == 0;
}
