using System.Collections.Concurrent;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Batch restore, delete, mirror sync, and preview operations.
/// </summary>
public partial class RestoreService
{
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

        // Shared memory budget for the entire mirror sync operation.
        var mirrorConfig = _databaseService.GetConfiguration();
        using var memoryBudget = MemoryBudget.FromConfig(mirrorConfig, FileStreamOverhead);

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
                        backupFile, targetPath, overwriteExisting: true, individualFileProgress, logPrefix, memoryBudget, ct);

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
        MemoryBudget? memoryBudget,
        CancellationToken cancellationToken)
    {
        try
        {
            var success = await RestoreFileAsync(file, targetPath, overwriteExisting,
                fileProgress, memoryBudget, cancellationToken);

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

            // Create per-file diagnostics for the corrupted recovery attempt
            var diag = new FileOperationDiagnostics(file.LocalPath, "CorruptedRecovery", DiagnosticsDirectory);
            using var diagScope = diag.SetAmbient();
            diag.Record($"Original error: {ex.GetType().Name}: {ex.Message}");
            diag.Record($"File: size={file.FileSize:N0}, chunks={file.Chunks.Count}, hash={file.FileHash?[..8]}...");

            Log($"{logPrefix} Attempting corrupted recovery");
            StatusChanged?.Invoke(this, $"Attempting corrupted recovery: {Path.GetFileName(file.LocalPath)}");

            var recovery = await AttemptCorruptedRecoveryAsync(file, targetPath, diag, cancellationToken);
            if (recovery.HasValue)
            {
                var (recoveredPath, unrecoverableChunks) = recovery.Value;

                // When all chunks decrypted successfully (0 zero-filled), the data is fully
                // intact — only the CRC32 envelope check failed.  Promote the recovered file
                // to the original target path so the caller sees a normal restore.
                if (unrecoverableChunks == 0)
                {
                    // Flush diagnostics even on successful promotion — these CRC-only
                    // failures are the exact scenario we need to investigate.
                    diag.Record($"CRC-only recovery: all {file.Chunks.Count} chunks decrypted OK, promoting to original path");
                    var promoDiagPath = diag.Flush("CRC-only failure — promoted to original path (data intact)");
                    if (promoDiagPath != null)
                    {
                        Log($"{logPrefix} CRC recovery diagnostics written to {promoDiagPath}");
                    }

                    try
                    {
                        if (File.Exists(targetPath))
                            File.Delete(targetPath);
                        File.Move(recoveredPath, targetPath);

                        // Set file timestamps to match original
                        File.SetLastWriteTimeUtc(targetPath, file.LastModified);

                        Log($"{logPrefix} PROMOTED recovered file to original path (CRC-only failure, data intact)");
                        StatusChanged?.Invoke(this, $"Restored (CRC recovery): {Path.GetFileName(targetPath)}");

                        // Try to remove the __corrupted__ directory if it's now empty
                        var corruptedDir = Path.GetDirectoryName(recoveredPath);
                        if (!string.IsNullOrEmpty(corruptedDir) && Directory.Exists(corruptedDir) &&
                            !Directory.EnumerateFileSystemEntries(corruptedDir).Any())
                        {
                            try { Directory.Delete(corruptedDir); } catch { /* best effort */ }
                        }

                        return (FileRestoreOutcome.Success, null, 0);
                    }
                    catch (Exception moveEx)
                    {
                        Log($"{logPrefix} Failed to promote recovered file: {moveEx.Message}, keeping in __corrupted__");
                        // Fall through to report as corrupted recovery
                    }
                }

                var status = unrecoverableChunks > 0
                    ? $"Recovered with {unrecoverableChunks} zero-filled chunk(s)"
                    : "Recovered (CRC mismatch only, data intact)";
                Log($"{logPrefix} RECOVERED to {recoveredPath} — {status}");
                ErrorOccurred?.Invoke(this, $"Corrupted recovery: {file.LocalPath} → {recoveredPath} ({status})");

                // Always flush diagnostics for corrupted recoveries — the data
                // is needed to investigate the root cause of CRC failures.
                var diagPath = diag.Flush($"Corrupted recovery: {status}");
                if (diagPath != null)
                {
                    Log($"{logPrefix} Recovery diagnostics written to {diagPath}");
                }

                return (FileRestoreOutcome.CorruptedRecovery, recoveredPath, unrecoverableChunks);
            }

            Log($"{logPrefix} RECOVERY FAILED");
            ErrorOccurred?.Invoke(this, $"Failed to restore {file.LocalPath}: integrity error and recovery failed");
            diag.Flush("Recovery failed entirely");
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
        FileOperationDiagnostics? diag,
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
                    diag?.RecordChunk("BestEffortOK", chunk.Index, chunk.Hash, chunkData.Length,
                        extra: $"blob={chunk.BlobName}, expectedLen={chunk.Length}");
                    await outputStream.WriteAsync(chunkData, cancellationToken);
                    recoveredChunks++;
                }
                else
                {
                    diag?.RecordChunk("BestEffortFAIL", chunk.Index, chunk.Hash, chunk.Length,
                        extra: $"blob={chunk.BlobName}, zero-filled");
                    // Zero-fill in blocks using shared buffer to avoid large allocations
                    var remaining = chunk.Length;
                    while (remaining > 0)
                    {
                        var writeSize = Math.Min(remaining, zeroBuffer.Length);
                        await outputStream.WriteAsync(zeroBuffer.AsMemory(0, writeSize), cancellationToken);
                        remaining -= writeSize;
                    }
                    unrecoverableChunks++;
                    Log($"AttemptCorruptedRecoveryAsync: '{Path.GetFileName(file.LocalPath)}' chunk {chunk.Index} UNRECOVERABLE, zero-filled ({chunk.Length} bytes)");
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
        const long SmallFileThreshold = 16L * 1024 * 1024; 
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

        Log($"RestoreFilesWithRemappingAsync: {smallFiles.Count} small files (≤{SmallFileThreshold / (1024 * 1024)} MB, max {MaxParallelSmallFiles} concurrent), " +
            $"{largeFiles.Count} large files (max {MaxParallelFileRestores} concurrent)");

        // Create a shared memory budget from the user's config.
        // All concurrent file restores share this single budget so the total
        // in-flight chunk memory stays within the user's limit.
        var config = _databaseService.GetConfiguration();
        using var memoryBudget = MemoryBudget.FromConfig(config, FileStreamOverhead);

        Log($"RestoreFilesWithRemappingAsync: memoryBudget={(!memoryBudget.IsUnlimited ? $"{config.MemoryLimitMB} MB" : "unlimited")}");

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
                        completedFiles, memoryBudget, ct);
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
                        completedFiles, memoryBudget, ct);
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
    /// Shared by <see cref="RestoreFilesWithRemappingAsync"/> and <see cref="MirrorSyncToLocalAsync"/>.
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
        MemoryBudget? memoryBudget,
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
            file, normalizedPath, overwriteExisting, individualFileProgress, logPrefix, memoryBudget, cancellationToken);

        lock (resultLock)
        {
            ApplyRestoreOutcome(result, outcome, file, normalizedPath, recoveredPath, unrecoverableChunks);
        }
    }
}
