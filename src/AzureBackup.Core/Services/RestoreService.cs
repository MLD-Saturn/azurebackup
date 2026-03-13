using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
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
    // Memory bounded by: MaxParallelChunkDownloads * MaxChunkSize = 12 * 128 MB = 1.5 GB max
    // In practice, most chunks are much smaller, so typical usage is ~100-500 MB
    private const int MaxParallelChunkDownloads = 12;

    public event EventHandler<RestoreProgressEventArgs>? ProgressChanged;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;
    
    /// <summary>
    /// Event for detailed debug/diagnostic logging.
    /// </summary>
    public event EventHandler<string>? DiagnosticLog;
    
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        DiagnosticLog?.Invoke(this, $"[{timestamp}] [Restore] {message}");
    }

    /// <summary>
    /// Validates that a restore path is safe and not targeting sensitive system directories.
    /// </summary>
    private static void ValidateRestorePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        
        // Get the full path to normalize it and resolve any relative components
        var fullPath = Path.GetFullPath(path);
        
        // Check for path traversal attempts
        if (fullPath.Contains("..", StringComparison.Ordinal))
        {
            throw new SecurityPolicyException(
                "Invalid restore path: path traversal detected",
                SecurityPolicyType.InvalidBlobName);
        }
        
        // Define sensitive system directories that should not be restore targets
        var sensitiveDirectories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
        };
        
        foreach (var sensitiveDir in sensitiveDirectories)
        {
            if (!string.IsNullOrEmpty(sensitiveDir) && 
                fullPath.StartsWith(sensitiveDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityPolicyException(
                    $"Cannot restore to protected system directory: {sensitiveDir}",
                    SecurityPolicyType.InvalidBlobName);
            }
        }
        
        // Validate the path doesn't contain invalid characters
        var invalidChars = Path.GetInvalidPathChars();
        if (path.IndexOfAny(invalidChars) >= 0)
        {
            throw new SecurityPolicyException(
                "Invalid restore path: contains invalid characters",
                SecurityPolicyType.InvalidBlobName);
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
            
            if (sortedChunks.Count > 1)
            {
                Log($"RestoreFileAsync: Using bounded parallel downloads (max={MaxParallelChunkDownloads})");
                // Use bounded producer-consumer pattern with channels
                await RestoreWithBoundedParallelDownloadsAsync(
                    sortedChunks, file, tempPath, progress, 
                    p => currentBytes = p, cancellationToken);
            }
            else
            {
                Log("RestoreFileAsync: Single chunk, downloading directly");
                // Single chunk - download and write directly
                await using FileStream outputStream = new(tempPath, FileMode.Create, FileAccess.Write, 
                    FileShare.None, bufferSize: 81920, useAsync: true);

                foreach (var chunk in sortedChunks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Log($"RestoreFileAsync: Downloading chunk {chunk.Index} ({chunk.Length} bytes) blob={chunk.BlobName}");
                    var chunkData = await _blobService.DownloadChunkAsync(chunk.BlobName, cancellationToken);
                    
                    // Verify chunk size and hash match metadata
                    Log($"RestoreFileAsync: Verifying chunk {chunk.Index} integrity");
                    VerifyChunkIntegrity(chunkData, chunk, file.LocalPath);
                    
                    await outputStream.WriteAsync(chunkData, cancellationToken);

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
            }
            
            // Verify restored file integrity
            Log($"RestoreFileAsync: Verifying file integrity of temp file: {tempPath}");
            StatusChanged?.Invoke(this, $"Verifying integrity: {Path.GetFileName(targetPath)}");
            var restoredHash = await ComputeFileHashAsync(tempPath, cancellationToken);
            
            if (!string.Equals(restoredHash, file.FileHash, StringComparison.OrdinalIgnoreCase))
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
    /// Computes SHA-256 hash of a file for integrity verification.
    /// </summary>
    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, 
            FileShare.Read, bufferSize: 81920, useAsync: true);
        
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Computes SHA-256 hash of chunk data for integrity verification.
    /// </summary>
    private static string ComputeChunkHash(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return Convert.ToHexString(hash);
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
        var actualHash = ComputeChunkHash(chunkData);
        if (!string.Equals(actualHash, chunk.Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new DataIntegrityException(
                $"Chunk {chunk.Index} hash mismatch: expected {chunk.Hash}, got {actualHash}",
                filePath);
        }
    }

    /// <summary>
    /// Restores a file using bounded parallel downloads with a producer-consumer pattern.
    /// Downloads chunks in parallel but writes them sequentially, limiting memory usage
    /// to approximately MaxParallelChunkDownloads chunks at any time.
    /// </summary>
    private async Task RestoreWithBoundedParallelDownloadsAsync(
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
        Log($"BoundedParallelDownload: Starting for '{Path.GetFileName(file.LocalPath)}' - " +
            $"{sortedChunks.Count} chunks, max concurrency={MaxParallelChunkDownloads}, " +
            $"maxChunkSize={maxChunkBytes:N0} bytes, totalChunkBytes={totalChunkBytes:N0}, " +
            $"GC.TotalMemory={gcMemBefore:N0} bytes");
        
        // Log if memory pressure could be dangerous:
        // Each parallel chunk needs ~3x memory (stream buffer + encrypted array + decrypted array)
        var estimatedPeakMemory = (long)maxChunkBytes * 3 * MaxParallelChunkDownloads;
        if (estimatedPeakMemory > 1_000_000_000) // >1 GB estimated peak
        {
            Log($"BoundedParallelDownload: WARNING - Estimated peak memory={estimatedPeakMemory:N0} bytes " +
                $"({MaxParallelChunkDownloads} parallel x {maxChunkBytes:N0} bytes x 3 copies). " +
                $"Risk of OutOfMemoryException for large chunks.");
        }
        
        var channelOptions = new BoundedChannelOptions(MaxParallelChunkDownloads)
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
            try
            {
                Log("BoundedParallelDownload.Producer: Starting download tasks");
                using SemaphoreSlim semaphore = new(MaxParallelChunkDownloads);
                var downloadTasks = new List<Task>();
                
                foreach (var chunk in sortedChunks)
                {
                    linkedCts.Token.ThrowIfCancellationRequested();
                    
                    await semaphore.WaitAsync(linkedCts.Token);
                    
                    var downloadTask = Task.Run(async () =>
                    {
                        try
                        {
                            linkedCts.Token.ThrowIfCancellationRequested();
                            
                            var chunkData = await _blobService.DownloadChunkAsync(chunk.BlobName, linkedCts.Token);
                            
                            VerifyChunkIntegrity(chunkData, chunk, file.LocalPath);
                            
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
                
                try
                {
                    await Task.WhenAll(downloadTasks);
                    Log("BoundedParallelDownload.Producer: All downloads completed");
                }
                catch (OperationCanceledException) when (downloadException != null)
                {
                    Log($"BoundedParallelDownload.Producer: Downloads cancelled due to error: {downloadException.Message}");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"BoundedParallelDownload.Producer: EXCEPTION in producer: {ex.GetType().Name}: {ex.Message}");
                downloadException ??= ex;
            }
            finally
            {
                channel.Writer.Complete(downloadException);
                Log("BoundedParallelDownload.Producer: Channel writer completed");
            }
        }, linkedCts.Token);
        
        // Consumer task: Read chunks from channel and write to file in order
        var writerTask = Task.Run(async () =>
        {
            var pendingChunks = new Dictionary<int, byte[]>();
            int nextChunkToWrite = 0;
            
            Log($"BoundedParallelDownload.Consumer: Opening output file: {tempPath}");
            await using FileStream outputStream = new(tempPath, FileMode.Create, FileAccess.Write, 
                FileShare.None, bufferSize: 81920, useAsync: true);
            
            try
            {
                await foreach (var (index, data) in channel.Reader.ReadAllAsync(linkedCts.Token))
                {
                    if (index == nextChunkToWrite)
                    {
                        await outputStream.WriteAsync(data, linkedCts.Token);
                        currentBytes += data.Length;
                        chunksWritten++;
                        nextChunkToWrite++;
                        updateCurrentBytes(currentBytes);
                        progress?.Report((currentBytes, file.FileSize));
                        
                        while (pendingChunks.TryGetValue(nextChunkToWrite, out var pendingData))
                        {
                            pendingChunks.Remove(nextChunkToWrite);
                            await outputStream.WriteAsync(pendingData, linkedCts.Token);
                            currentBytes += pendingData.Length;
                            chunksWritten++;
                            nextChunkToWrite++;
                            updateCurrentBytes(currentBytes);
                            progress?.Report((currentBytes, file.FileSize));
                        }
                        
                        if (chunksWritten % 50 == 0)
                        {
                            Log($"BoundedParallelDownload.Consumer: Written {chunksWritten}/{sortedChunks.Count} chunks, {currentBytes} bytes, {pendingChunks.Count} buffered");
                        }
                    }
                    else
                    {
                        pendingChunks[index] = data;
                        if (pendingChunks.Count % 10 == 0 || pendingChunks.Count > MaxParallelChunkDownloads)
                        {
                            var bufferedBytes = pendingChunks.Values.Sum(d => (long)d.Length);
                            Log($"BoundedParallelDownload.Consumer: Buffering chunk {index} (waiting for {nextChunkToWrite}), " +
                                $"{pendingChunks.Count} chunks buffered ({bufferedBytes:N0} bytes), " +
                                $"GC.TotalMemory={GC.GetTotalMemory(false):N0}");
                        }
                    }
                }
                
                await outputStream.FlushAsync(linkedCts.Token);
                Log($"BoundedParallelDownload.Consumer: Flushed output, {chunksWritten} chunks written, {currentBytes} bytes");
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
    }

    /// <summary>
    /// Restores multiple files to a specified directory.
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
        
        RestoreResult result = new();
        var fileList = files.ToList();

        StatusChanged?.Invoke(this, $"Starting restore of {fileList.Count} files");

        for (var i = 0; i < fileList.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = fileList[i];
            progress?.Report((i + 1, fileList.Count, file.LocalPath));

            string targetPath;
            if (preserveFolderStructure)
            {
                // Find common root and preserve structure
                var relativePath = GetRelativePath(fileList.Select(f => f.LocalPath).ToList(), file.LocalPath);
                targetPath = Path.Combine(restoreDirectory, relativePath);
            }
            else
            {
                targetPath = Path.Combine(restoreDirectory, Path.GetFileName(file.LocalPath));
            }

            var success = await RestoreFileAsync(file, targetPath, overwriteExisting, 
                cancellationToken: cancellationToken);

            if (success)
            {
                result.SuccessfulFiles.Add(file.LocalPath);
                result.TotalBytesRestored += file.FileSize;
            }
            else
            {
                result.FailedFiles.Add(file.LocalPath);
            }
        }

        StatusChanged?.Invoke(this, $"Restore complete: {result.SuccessfulFiles.Count} succeeded, {result.FailedFiles.Count} failed");
        return result;
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

        // Find common root directory
        var directories = allPaths
            .Select(p => Path.GetDirectoryName(p) ?? string.Empty)
            .Where(d => !string.IsNullOrEmpty(d))
            .ToList();

        if (directories.Count == 0)
            return Path.GetFileName(currentPath);

        var commonRoot = directories[0];
        foreach (var dir in directories.Skip(1))
        {
            while (!dir.StartsWith(commonRoot, StringComparison.OrdinalIgnoreCase) && commonRoot.Length > 0)
            {
                var parentDir = Path.GetDirectoryName(commonRoot);
                if (string.IsNullOrEmpty(parentDir) || parentDir == commonRoot)
                {
                    commonRoot = string.Empty;
                    break;
                }
                commonRoot = parentDir;
            }
        }

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
            var metadataHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(file.LocalPath)));
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
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<MirrorSyncResult> MirrorSyncToLocalAsync(
        IEnumerable<BackedUpFile> backupFiles,
        string targetDirectory,
        string sourceBasePath,
        IProgress<(int current, int total, string file, string action)>? progress = null,
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

        // Build a set of expected files in target directory
        HashSet<string> expectedLocalFiles = new(StringComparer.OrdinalIgnoreCase);

        // Phase 1: Restore/update files from backup
        var totalOperations = fileList.Count;
        var currentOp = 0;

        foreach (var backupFile in fileList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentOp++;

            try
            {
                // Calculate target path by remapping base path
                var relativePath = GetRelativePathFromBase(backupFile.LocalPath, sourceBasePath);
                var targetPath = Path.Combine(targetDirectory, relativePath);
                expectedLocalFiles.Add(targetPath);

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
                        var localHash = await ComputeFileHashAsync(targetPath, cancellationToken);
                        if (string.Equals(localHash, backupFile.FileHash, StringComparison.OrdinalIgnoreCase))
                        {
                            result.FilesUnchanged++;
                            progress?.Report((currentOp, totalOperations, Path.GetFileName(targetPath), "Unchanged"));
                            continue;
                        }
                    }
                }

                // File is missing or outdated - restore it
                progress?.Report((currentOp, totalOperations, Path.GetFileName(targetPath), "Restoring"));
                var success = await RestoreFileAsync(backupFile, targetPath, overwriteExisting: true, 
                    cancellationToken: cancellationToken);

                if (success)
                {
                    result.FilesTransferred++;
                    result.BytesTransferred += backupFile.FileSize;
                    Log($"MirrorSyncToLocalAsync: Restored {Path.GetFileName(targetPath)}");
                }
                else
                {
                    result.FilesErrored++;
                    result.Errors.Add($"Failed to restore: {backupFile.LocalPath}");
                }
            }
            catch (Exception ex)
            {
                result.FilesErrored++;
                result.Errors.Add($"Error restoring {backupFile.LocalPath}: {ex.Message}");
                Log($"MirrorSyncToLocalAsync: Error restoring {backupFile.LocalPath}: {ex.Message}");
            }
        }

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

                if (!expectedLocalFiles.Contains(localFile))
                {
                    try
                    {
                        progress?.Report((currentOp, totalOperations, Path.GetFileName(localFile), "Deleting"));
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
            CleanEmptyDirectories(targetDirectory);
        }
        catch (Exception ex)
        {
            Log($"MirrorSyncToLocalAsync: Error during delete phase: {ex.Message}");
        }

        StatusChanged?.Invoke(this, 
            $"Mirror sync complete: {result.FilesTransferred} restored, {result.FilesDeleted} deleted, " +
            $"{result.FilesUnchanged} unchanged, {result.FilesErrored} errors");
        
        Log($"MirrorSyncToLocalAsync: Complete - {result.FilesTransferred} transferred, " +
            $"{result.FilesDeleted} deleted, {result.FilesUnchanged} unchanged");

        return result;
    }

    /// <summary>
    /// Gets the relative path from a base path.
    /// </summary>
    private static string GetRelativePathFromBase(string fullPath, string basePath)
    {
        // Normalize paths
        fullPath = Path.GetFullPath(fullPath);
        basePath = Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath.Substring(basePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(relative) ? Path.GetFileName(fullPath) : relative;
        }

        // If not under base path, just return the filename
        return Path.GetFileName(fullPath);
    }

    /// <summary>
    /// Removes empty directories recursively.
    /// </summary>
    private static void CleanEmptyDirectories(string directory)
    {
        foreach (var subDir in Directory.EnumerateDirectories(directory))
        {
            CleanEmptyDirectories(subDir);
            
            if (!Directory.EnumerateFileSystemEntries(subDir).Any())
            {
                try
                {
                    Directory.Delete(subDir);
                }
                catch
                {
                    // Ignore errors when cleaning up directories
                }
            }
        }
    }

    /// <summary>
    /// Restores files with path remapping support.
    /// Each file can have a custom target path.
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

        Log($"RestoreFilesWithRemappingAsync: Restoring {fileList.Count} files with path remapping");
        StatusChanged?.Invoke(this, $"Starting restore of {fileList.Count} files");

        for (var i = 0; i < fileList.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (file, targetPath) = fileList[i];
            Log($"RestoreFilesWithRemappingAsync: [{i + 1}/{fileList.Count}] '{Path.GetFileName(file.LocalPath)}' -> '{targetPath}' ({file.FileSize} bytes, {file.Chunks.Count} chunks)");
            progress?.Report((i + 1, fileList.Count, file.LocalPath));

            // Security: Validate target path doesn't contain suspicious patterns
            var normalizedPath = Path.GetFullPath(targetPath);
            if (normalizedPath.Contains(".." + Path.DirectorySeparatorChar) || 
                normalizedPath.Contains(".." + Path.AltDirectorySeparatorChar))
            {
                Log($"RestoreFilesWithRemappingAsync: Skipping suspicious path: {targetPath}");
                result.FailedFiles.Add(file.LocalPath);
                ErrorOccurred?.Invoke(this, $"Invalid target path (contains path traversal): {targetPath}");
                continue;
            }

            // Ensure target directory exists
            var targetDir = Path.GetDirectoryName(normalizedPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Log($"RestoreFilesWithRemappingAsync: Creating directory: {targetDir}");
                Directory.CreateDirectory(targetDir);
            }

            // Create byte-level progress reporter for this file
            var fileIndex = i;
            var individualFileProgress = fileByteProgress != null
                ? new Progress<(long current, long total)>(p =>
                    fileByteProgress.Report((p.current, file.FileSize, fileIndex)))
                : null;

            try
            {
                var success = await RestoreFileAsync(file, normalizedPath, overwriteExisting, 
                    individualFileProgress, cancellationToken);

                if (success)
                {
                    result.SuccessfulFiles.Add(normalizedPath);
                    result.TotalBytesRestored += file.FileSize;
                    Log($"RestoreFilesWithRemappingAsync: [{i + 1}/{fileList.Count}] OK");
                }
                else
                {
                    result.FailedFiles.Add(file.LocalPath);
                    Log($"RestoreFilesWithRemappingAsync: [{i + 1}/{fileList.Count}] FAILED (returned false)");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"RestoreFilesWithRemappingAsync: [{i + 1}/{fileList.Count}] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
                Log($"RestoreFilesWithRemappingAsync: StackTrace: {ex.StackTrace}");
                result.FailedFiles.Add(file.LocalPath);
                ErrorOccurred?.Invoke(this, $"Failed to restore {file.LocalPath}: {ex.Message}");
            }
        }

        Log($"RestoreFilesWithRemappingAsync: Complete - {result.SuccessfulFiles.Count} succeeded, {result.FailedFiles.Count} failed, {result.TotalBytesRestored} bytes");
        StatusChanged?.Invoke(this, $"Restore complete: {result.SuccessfulFiles.Count} succeeded, {result.FailedFiles.Count} failed");
        return result;
    }

    #region Preview Generation Methods

    /// <summary>
    /// Generates a preview of a mirror sync operation without making any changes.
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

        HashSet<string> expectedLocalFiles = new(StringComparer.OrdinalIgnoreCase);

        // Check each backup file
        foreach (var backupFile in fileList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = GetRelativePathFromBase(backupFile.LocalPath, sourceBasePath);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            expectedLocalFiles.Add(targetPath);

            if (File.Exists(targetPath))
            {
                FileInfo localInfo = new(targetPath);
                
                // Compare to see if update needed
                if (localInfo.Length == backupFile.FileSize && 
                    Math.Abs((localInfo.LastWriteTimeUtc - backupFile.LastModified).TotalSeconds) < 2)
                {
                    // Quick check - likely unchanged, but verify with hash
                    var localHash = await ComputeFileHashAsync(targetPath, cancellationToken);
                    if (string.Equals(localHash, backupFile.FileHash, StringComparison.OrdinalIgnoreCase))
                    {
                        preview.FilesToSkip.Add(new PreviewFileAction
                        {
                            FilePath = backupFile.LocalPath,
                            FileSize = backupFile.FileSize,
                            LastModified = backupFile.LastModified,
                            TargetPath = targetPath,
                            Action = FileActionType.Skip,
                            Reason = "File is identical"
                        });
                        continue;
                    }
                }

            // File exists but is different
                preview.FilesToOverwrite.Add(new PreviewFileAction
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
                preview.FilesToCreate.Add(new PreviewFileAction
                {
                    FilePath = backupFile.LocalPath,
                    FileSize = backupFile.FileSize,
                    LastModified = backupFile.LastModified,
                    TargetPath = targetPath,
                    Action = FileActionType.Create,
                    Reason = "File does not exist locally"
                });
            }
        }

        // Check for local files that don't exist in backup (will be deleted)
        if (Directory.Exists(targetDirectory))
        {
            var localFiles = Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories);
            foreach (var localFile in localFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!expectedLocalFiles.Contains(localFile))
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
    public long TotalBytesRestored { get; set; }

    public int TotalFilesProcessed => SuccessfulFiles.Count + FailedFiles.Count;
    public bool IsSuccess => FailedFiles.Count == 0;
}
