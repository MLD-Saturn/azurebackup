using System.Diagnostics;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Scan, sync, mirror, preview, and batch backup operations.
/// </summary>
public partial class BackupOrchestrator
{
    /// <summary>
    /// Performs a full scan and backup of all watched folders.
    /// <para>
    /// B42: previously this method only enqueued files into the
    /// pending-changes queue, which the watcher then processed via the
    /// normal metadata-skip path -- so a "force full scan" against an
    /// unchanged corpus uploaded nothing, despite the UI promising
    /// "re-upload ALL files (ignoring backup history)". The method now
    /// invokes <see cref="BackupFilesAsync"/> with <c>forceReupload</c>
    /// = <c>true</c> so every file's chunks are re-encrypted and
    /// re-uploaded with overwrite semantics.
    /// </para>
    /// </summary>
    public async Task PerformFullScanAsync(IProgress<(int current, int total, string file)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = _databaseService.GetConfiguration();
        List<string> allFiles = new();

        StatusChanged?.Invoke(this, "Scanning folders...");
        Log("PerformFullScanAsync: Starting full scan of all watched folders (forceReupload=true)");

        // Scan all watched folders
        foreach (var folder in config.WatchedFolders.Where(f => f.IsEnabled))
        {
            var files = await _fileWatcherService.ScanFolderAsync(folder, cancellationToken);
            allFiles.AddRange(files);
            Log($"PerformFullScanAsync: Found {files.Count} files in {folder.Path}");
        }

        StatusChanged?.Invoke(this, $"Found {allFiles.Count} files to re-upload");

        // Adapt the simple (current,total,file) UI reporter into the richer
        // BackupFilesAsync progress shape so existing callers keep working.
        IProgress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)>? adapted = null;
        if (progress != null)
        {
            int reportedCount = 0;
            adapted = new Progress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)>(p =>
            {
                if (p.currentFileBytes >= p.currentFileSize && p.currentFileSize > 0)
                {
                    var done = Interlocked.Increment(ref reportedCount);
                    progress.Report((done, p.totalFiles, p.fileName));
                }
            });
        }

        await BackupFilesAsync(allFiles, adapted, forceReupload: true, cancellationToken);

        StatusChanged?.Invoke(this, $"Full scan complete: {allFiles.Count} files re-uploaded");
        Log($"PerformFullScanAsync: Complete - {allFiles.Count} files re-uploaded with forceReupload=true");
    }

    /// <summary>
    /// Performs an initial sync of all watched folders with Azure storage.
    /// Only queues files that are new or have changed since last backup.
    /// This is more efficient than PerformFullScanAsync for subsequent syncs.
    /// Uses bulk database lookups instead of per-file queries for performance at scale.
    /// </summary>
    /// <param name="progress">Progress reporter for UI updates</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Summary of sync results (total files, new files queued, unchanged files skipped)</returns>
    public async Task<InitialSyncResult> PerformInitialSyncAsync(
        IProgress<(int current, int total, string file, string status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Log("PerformInitialSyncAsync: Starting initial sync");
        var config = _databaseService.GetConfiguration();
        InitialSyncResult result = new();
        List<string> allFiles = new();

        StatusChanged?.Invoke(this, "Scanning watched folders for sync...");

        // Phase 1: Scan all watched folders to find files
        foreach (var folder in config.WatchedFolders.Where(f => f.IsEnabled))
        {
            Log($"PerformInitialSyncAsync: Scanning folder {folder.Path}");
            var files = await _fileWatcherService.ScanFolderAsync(folder, cancellationToken);
            allFiles.AddRange(files);
        }

        result.TotalFilesScanned = allFiles.Count;
        StatusChanged?.Invoke(this, $"Found {allFiles.Count} files - checking which need backup...");
        Log($"PerformInitialSyncAsync: Found {allFiles.Count} total files to check");

        // Phase 2: Bulk-load all backup records and pending paths into memory
        // This replaces N sequential DB lookups with 2 bulk queries
        StatusChanged?.Invoke(this, "Loading backup state from database...");
        var backedUpFiles = _databaseService.GetAllBackedUpFiles()
            .ToDictionary(f => f.LocalPath, f => f, StringComparer.OrdinalIgnoreCase);
        var pendingPaths = _databaseService.GetAllPendingChangePaths();
        Log($"PerformInitialSyncAsync: Loaded {backedUpFiles.Count} backup records and {pendingPaths.Count} pending paths");

        // Accumulate queued changes for a single batched commit at the end of the
        // loop. At large file counts (tens of thousands) this turns N per-file
        // transactions into one, shaving several seconds of wall-clock time.
        var pendingBatch = new List<FileChangeEvent>();

        // Phase 3: Compare each file against in-memory lookup tables
        for (var i = 0; i < allFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filePath = allFiles[i];
            var fileName = Path.GetFileName(filePath);

            try
            {
                // Check if file is already pending (in-memory set lookup)
                if (pendingPaths.Contains(filePath))
                {
                    result.AlreadyPending++;
                    progress?.Report((i + 1, allFiles.Count, fileName, "Already queued"));
                    continue;
                }

                // Get existing backup record (in-memory dictionary lookup)
                backedUpFiles.TryGetValue(filePath, out var existingBackup);

                if (existingBackup == null)
                {
                    // New file - never backed up
                    pendingBatch.Add(new FileChangeEvent
                    {
                        FilePath = filePath,
                        ChangeType = FileChangeType.Created,
                        DetectedAt = DateTime.UtcNow
                    });
                    result.NewFilesQueued++;
                    progress?.Report((i + 1, allFiles.Count, fileName, "New - queued"));
                    Log($"PerformInitialSyncAsync: New file queued: {fileName}");
                }
                else if (existingBackup.Status == BackupStatus.Completed)
                {
                    // File was previously backed up - check if it changed
                    FileInfo fileInfo = new(filePath);

                    // Quick check: compare last modified time and size
                    if (fileInfo.LastWriteTimeUtc > existingBackup.LastModified || 
                        fileInfo.Length != existingBackup.FileSize)
                    {
                        // File appears changed - queue for backup (hash will be verified during backup)
                        pendingBatch.Add(new FileChangeEvent
                        {
                            FilePath = filePath,
                            ChangeType = FileChangeType.Modified,
                            DetectedAt = DateTime.UtcNow
                        });
                        result.ModifiedFilesQueued++;
                        progress?.Report((i + 1, allFiles.Count, fileName, "Modified - queued"));
                        Log($"PerformInitialSyncAsync: Modified file queued: {fileName}");
                    }
                    else
                    {
                        // File unchanged
                        result.UnchangedFiles++;
                        progress?.Report((i + 1, allFiles.Count, fileName, "Unchanged"));
                    }
                }
                else if (existingBackup.Status == BackupStatus.Failed)
                {
                    // Retry previously failed file
                    pendingBatch.Add(new FileChangeEvent
                    {
                        FilePath = filePath,
                        ChangeType = FileChangeType.Modified,
                        DetectedAt = DateTime.UtcNow
                    });
                    result.RetriedFiles++;
                    progress?.Report((i + 1, allFiles.Count, fileName, "Retrying failed"));
                    Log($"PerformInitialSyncAsync: Retrying failed file: {fileName}");
                }
                else
                {
                    // Excluded or other status - skip
                    result.SkippedFiles++;
                    progress?.Report((i + 1, allFiles.Count, fileName, "Skipped"));
                }
            }
            catch (Exception ex)
            {
                Log($"PerformInitialSyncAsync: Error checking file {fileName}: {ex.Message}");
                result.ErrorFiles++;
                progress?.Report((i + 1, allFiles.Count, fileName, $"Error: {ex.Message}"));
            }
        }

        // Flush the accumulated pending changes in a single transaction.
        if (pendingBatch.Count > 0)
        {
            _databaseService.QueueFileChangesBatch(pendingBatch);
            Log($"PerformInitialSyncAsync: Persisted {pendingBatch.Count} pending changes in one batch");
        }

        var queuedCount = result.NewFilesQueued + result.ModifiedFilesQueued + result.RetriedFiles;
        StatusChanged?.Invoke(this, 
            $"Sync complete: {queuedCount} files queued for backup, {result.UnchangedFiles} unchanged");
        Log($"PerformInitialSyncAsync: Complete - {queuedCount} queued, {result.UnchangedFiles} unchanged");


        return result;
    }

    /// <summary>
    /// Performs a mirror sync from a local folder to Azure backup.
    /// This will:
    /// 1. Backup files that are new or modified locally
    /// 2. Mark files in Azure as deleted if they no longer exist locally
    /// 3. Skip files that are identical
    /// </summary>
    /// <param name="localFolder">Local folder to sync from</param>
    /// <param name="progress">Progress reporter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<MirrorSyncResult> MirrorSyncToAzureAsync(
        WatchedFolder localFolder,
        IProgress<(int current, int total, string file, string action)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localFolder);

        Log($"MirrorSyncToAzureAsync: Starting mirror sync from '{localFolder.Path}' to Azure");
        MirrorSyncResult result = new();
        var mirrorAzureStopwatch = Stopwatch.StartNew();
        // Snapshot CRC counters so we can record the per-op delta in the
        // OperationMetrics record below. Process-cumulative counters would
        // grow monotonically across runs and obscure regressions.
        var crcFailStart = _blobService.TotalCrcFailures;
        var crcRetryStart = _blobService.TotalCrcRetries;

        StatusChanged?.Invoke(this, $"Mirror sync: scanning {localFolder.Path}");

        // Phase 1: Scan local folder for files
        var localFiles = await _fileWatcherService.ScanFolderAsync(localFolder, cancellationToken);
        HashSet<string> localFilePaths = new(localFiles, StringComparer.OrdinalIgnoreCase);

        Log($"MirrorSyncToAzureAsync: Found {localFiles.Count} local files");

        // Phase 2: Get existing backups for this folder
        var existingBackups = _databaseService.GetAllBackedUpFiles()
            .Where(f => f.LocalPath.StartsWith(localFolder.Path, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.LocalPath, StringComparer.OrdinalIgnoreCase);

        // Phase 3: Classify files as unchanged or needing backup
        var filesToBackup = new List<string>();
        foreach (var localFilePath in localFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(localFilePath);

            if (existingBackups.TryGetValue(localFilePath, out var existingBackup))
            {
                FileInfo fileInfo = new(localFilePath);
                if (fileInfo.Length == existingBackup.FileSize &&
                    Math.Abs((fileInfo.LastWriteTimeUtc - existingBackup.LastModified).TotalSeconds) < 2)
                {
                    result.FilesUnchanged++;
                    progress?.Report((result.FilesUnchanged, localFiles.Count + existingBackups.Count, fileName, "Unchanged"));
                    continue;
                }
            }

            filesToBackup.Add(localFilePath);
        }

        var totalOperations = localFiles.Count + existingBackups.Count;

        // Phase 4: Backup new and modified files using the shared parallel core.
        // This gives MirrorSyncToAzureAsync the same parallelism, memory budget,
        // and per-file metrics recording as BackupFilesAsync.
        // B54: hoist effective file concurrency out of the if-block so the
        // post-loop metrics record the same value the loop actually used.
        var effectiveFileConcurrency = EffectiveMaxParallelFileBackups;
        if (filesToBackup.Count > 0)
        {
            var config = _databaseService.GetConfiguration();
            using var memoryBudget = MemoryBudget.FromConfig(config, CdcBufferOverhead);
            // B37: a single large-chunk ChunkBufferPool spans the
            // entire operation so the recycler is shared across files.
            // B52: cap the pool's cached residency at 25% of the
            // configured budget so the recycler cannot drift past
            // the user's MemoryLimitMB ceiling.
            // B72 (W5 Phase 4): attribute the pool's cached retention
            // to the same MemoryBudget so the cached bytes show up
            // inside PeakUsedBytes instead of the pre-B72 unaccounted
            // residency gap; producers stalling on AcquireAsync now
            // see the correct headroom.
            using var largeChunkPool = new ChunkBufferPool(
                ChunkBufferPool.LargeChunkBucketSizes,
                ComputePoolCapBytes(memoryBudget),
                memoryBudget);
            // B69 (W5 Phase 3 Commit 1): a single small-chunk
            // ChunkBufferPool spans the entire operation so the
            // small-chunk recycler shares the operation's lifetime and
            // the per-core ArrayPool<byte>.Shared tier caches no
            // longer leak residency outside the budget. Cap derived
            // from the active budget so the pool's hidden residency
            // cannot drift past the user's MemoryLimitMB ceiling.
            // B72: pool retention is now charged against the budget
            // (see large-pool comment above for the rationale).
            using var smallChunkPool = new ChunkBufferPool(
                ChunkBufferPool.SmallChunkBucketSizes,
                ComputeSmallPoolCapBytes(memoryBudget),
                memoryBudget);
            // B36: emit a periodic memory snapshot through StatusChanged so
            // the always-visible log pane records budget vs working-set
            // drift during the operation. Wired before BackupFilesCoreAsync
            // so the initial sample captures pre-fan-out state.
            using var memReporter = new BackupMemoryReporter(
                memoryBudget,
                opLabel: "mirror",
                emit: line => StatusChanged?.Invoke(this, line),
                interval: MemoryReporterIntervalOverride,
                largeChunkPool: largeChunkPool,
                smallChunkPool: smallChunkPool);

            // B54: clamp file-level fan-out against the active budget so a
            // small MemoryLimitMB does not over-subscribe in-flight residency.
            effectiveFileConcurrency =
                ComputeEffectiveFileConcurrency(memoryBudget, EffectiveMaxParallelFileBackups);

            Log($"MirrorSyncToAzureAsync: Backing up {filesToBackup.Count} new/modified files " +
                $"(max {effectiveFileConcurrency} concurrent, " +
                $"memoryBudget={(!memoryBudget.IsUnlimited ? $"{config.MemoryLimitMB} MB" : "unlimited")})");

            Metrics?.RecordContext("mirror-to-azure", config.MemoryLimitEnabled ? config.MemoryLimitMB : 0, config.MemoryLimitEnabled);

            // Adapt BackupFilesCoreAsync progress to the mirror progress format.
            // B19: the inner tuple's fileIndex is now a STABLE per-file id
            // (the file's slot in filesToBackup), no longer a running
            // completion counter. The mirror UI wants "operation X of M"
            // where X advances by one per file COMPLETED. We track that
            // ourselves and only push a mirror line on completion reports
            // (detected by currentFileBytes == currentFileSize); per-byte
            // callbacks are absorbed without spamming the top-line string.
            var backupBaseOp = result.FilesUnchanged;
            var mirrorCompleted = 0;
            var adaptedProgress = progress != null
                ? new Progress<(int fileIndex, int totalFiles, string fileName,
                    long bytesProcessed, long totalBytes,
                    long currentFileBytes, long currentFileSize)>(p =>
                {
                    if (p.currentFileBytes >= p.currentFileSize)
                    {
                        var ops = Interlocked.Increment(ref mirrorCompleted);
                        progress.Report((backupBaseOp + ops, totalOperations, p.fileName, "Backing up"));
                    }
                })
                : null;

            var (completed, failed, bytes) = (0, 0, 0L);
            try
            {
                (completed, failed, bytes) = await BackupFilesCoreAsync(
                    filesToBackup, memoryBudget, largeChunkPool, smallChunkPool, adaptedProgress,
                    forceReupload: false, effectiveFileConcurrency, cancellationToken);
            }
            catch (Exception ex) when (TryExtractAuthFailure(ex, out var auth))
            {
                InvalidateAzureCredential(auth!);
                throw;
            }

            result.FilesTransferred = completed;
            result.FilesErrored = failed;
            result.BytesTransferred = bytes;
        }

        // Phase 5: Mark deleted files (files in backup but not locally)
        var deleteBaseOp = result.FilesUnchanged + filesToBackup.Count;
        var deleteIndex = 0;
        foreach (var (backupPath, backupFile) in existingBackups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            deleteIndex++;

            if (!localFilePaths.Contains(backupPath))
            {
                try
                {
                    progress?.Report((deleteBaseOp + deleteIndex, totalOperations, Path.GetFileName(backupPath), "Marking deleted"));

                    if (_chunkIndexService != null)
                    {
                        var deletedChunks = await _chunkIndexService.RemoveFileReferencesAsync(
                            backupPath, cancellationToken);
                        if (deletedChunks > 0)
                        {
                            Log($"MirrorSyncToAzureAsync: Deleted {deletedChunks} orphaned chunks " +
                                $"for deleted file: {backupPath}");
                        }
                    }

                    backupFile.Status = BackupStatus.Excluded;
                    _databaseService.SaveBackedUpFile(backupFile);
                    result.FilesDeleted++;

                    Log($"MirrorSyncToAzureAsync: Marked as deleted: {backupPath}");
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Failed to mark deleted: {backupPath}: {ex.Message}");
                }
            }
        }

        StatusChanged?.Invoke(this, 
            $"Mirror sync complete: {result.FilesTransferred} backed up, {result.FilesDeleted} marked deleted, " +
            $"{result.FilesUnchanged} unchanged, {result.FilesErrored} errors");

        Log($"MirrorSyncToAzureAsync: Complete - {result.FilesTransferred} transferred, " +
            $"{result.FilesDeleted} deleted, {result.FilesUnchanged} unchanged");

        mirrorAzureStopwatch.Stop();
        var mirrorElapsed = mirrorAzureStopwatch.Elapsed.TotalSeconds;
        Metrics?.RecordOperationAndFlush(new OperationMetrics
        {
            Operation = "mirror-to-azure",
            Files = localFiles.Count,
            Succeeded = result.FilesTransferred,
            Failed = result.FilesErrored,
            Bytes = result.BytesTransferred,
            ElapsedSeconds = mirrorElapsed,
            ThroughputMBps = mirrorElapsed > 0 ? result.BytesTransferred / mirrorElapsed / (1024 * 1024) : 0,
            FileConcurrency = effectiveFileConcurrency,
            MemoryBudgetMb = filesToBackup.Count > 0 ? (int)(_databaseService.GetConfiguration().MemoryLimitMB) : 0,
            CrcFailCount = (int)(_blobService.TotalCrcFailures - crcFailStart),
            CrcRetryCount = (int)(_blobService.TotalCrcRetries - crcRetryStart)
        });

        return result;
    }

    /// <summary>
    /// Generates a preview of what a backup sync operation will do without making changes.
    /// This allows showing the user what will be uploaded before starting.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview of the backup operation</returns>
    public async Task<OperationPreview> PreviewBackupSyncAsync(CancellationToken cancellationToken = default)
    {
        Log("PreviewBackupSyncAsync: Generating backup preview");
        var config = _databaseService.GetConfiguration();
        
        OperationPreview preview = new()
        {
            OperationType = OperationType.Backup,
            OperationDescription = "Sync local files to Azure backup",
            SourceDescription = $"{config.WatchedFolders.Count(f => f.IsEnabled)} watched folder(s)",
            TargetDescription = $"Azure Storage ({config.ContainerName ?? "backup"})"
        };

        // Scan all watched folders
        List<string> allFiles = new();
        foreach (var folder in config.WatchedFolders.Where(f => f.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = await _fileWatcherService.ScanFolderAsync(folder, cancellationToken);
            allFiles.AddRange(files);
        }

        Log($"PreviewBackupSyncAsync: Found {allFiles.Count} files to check");

        // Get list of files that actually exist in Azure for validation
        HashSet<string>? azureFilePaths = null;
        if (_blobService.IsConnected)
        {
            try
            {
                var azureFiles = await _blobService.LoadAllFileMetadataAsync(cancellationToken: cancellationToken);
                azureFilePaths = azureFiles
                    .Select(f => f.LocalPath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                Log($"PreviewBackupSyncAsync: Found {azureFilePaths.Count} files in Azure for validation");
            }
            catch (Exception ex)
            {
                Log($"PreviewBackupSyncAsync: Could not fetch Azure file list for validation: {ex.Message}");
                // Continue without validation - will use local DB only
            }
        }

        // Compare each file with existing backup records
        foreach (var filePath in allFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var action = ClassifyFileForBackupPreview(filePath, azureFilePaths, "PreviewBackupSyncAsync");
                if (action == null) continue;

                switch (action.Action)
                {
                    case FileActionType.Create:
                        preview.FilesToCreate.Add(action);
                        break;
                    case FileActionType.Update:
                        preview.FilesToOverwrite.Add(action);
                        break;
                    default:
                        preview.FilesToSkip.Add(action);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"PreviewBackupSyncAsync: Error checking {filePath}: {ex.Message}");
            }
        }

        Log($"PreviewBackupSyncAsync: Preview complete - {preview.CreateCount} new, " +
            $"{preview.OverwriteCount} modified, {preview.SkipCount} unchanged");

        return preview;
    }

    /// <summary>
    /// Generates a preview of what backing up specific files will do (simple overload).
    /// </summary>
    public Task<OperationPreview> PreviewBackupFilesAsync(
        IList<string> filePaths,
        CancellationToken cancellationToken)
    {
        return PreviewBackupFilesAsync(filePaths, null, cancellationToken);
    }

    /// <summary>
    /// Generates a preview of what backing up specific files will do.
    /// Cross-references with actual Azure metadata to ensure accuracy.
    /// </summary>
    /// <param name="filePaths">List of file paths to preview</param>
    /// <param name="azureFilePaths">Optional set of file paths that actually exist in Azure (for validation)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview of the backup operation</returns>
    public Task<OperationPreview> PreviewBackupFilesAsync(
        IList<string> filePaths,
        ISet<string>? azureFilePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        Log($"PreviewBackupFilesAsync: Generating preview for {filePaths.Count} files" +
            (azureFilePaths != null ? $", validating against {azureFilePaths.Count} Azure files" : ""));


        var config = _databaseService.GetConfiguration();
        
        OperationPreview preview = new()
        {
            OperationType = OperationType.Backup,
            OperationDescription = $"Backup {filePaths.Count} selected file(s)",
            SourceDescription = "Selected local files",
            TargetDescription = $"Azure Storage ({config.ContainerName ?? "backup"})"
        };

        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var action = ClassifyFileForBackupPreview(filePath, azureFilePaths, "PreviewBackupFilesAsync");
                if (action == null)
                {
                    Log($"PreviewBackupFilesAsync: File not found: {filePath}");
                    continue;
                }

                switch (action.Action)
                {
                    case FileActionType.Create:
                        preview.FilesToCreate.Add(action);
                        break;
                    case FileActionType.Update:
                        preview.FilesToOverwrite.Add(action);
                        break;
                    default:
                        preview.FilesToSkip.Add(action);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"PreviewBackupFilesAsync: Error checking {filePath}: {ex.Message}");
            }
        }

        Log($"PreviewBackupFilesAsync: Preview complete - {preview.CreateCount} new, " +
            $"{preview.OverwriteCount} modified, {preview.SkipCount} unchanged");

        return Task.FromResult(preview);
    }

    /// <summary>
    /// Backs up specific files to Azure using parallel file processing.
    /// </summary>
    /// <param name="filePaths">List of file paths to backup</param>
    /// <param name="progress">
    /// Progress reporter. <c>fileIndex</c> is the file's STABLE position
    /// in <paramref name="filePaths"/> -- safe to use as the key for a
    /// per-file UI row across the file's entire lifetime, even when
    /// multiple workers report concurrently. The other tuple fields are:
    /// <c>totalFiles</c> (overall count), <c>fileName</c>, <c>bytesProcessed</c>
    /// (sum across all files), <c>totalBytes</c> (sum of all file sizes),
    /// <c>currentFileBytes</c> (this file's progress so far), and
    /// <c>currentFileSize</c> (this file's full size).
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task BackupFilesAsync(
        IList<string> filePaths,
        IProgress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)>? progress = null,
        CancellationToken cancellationToken = default)
        => await BackupFilesAsync(filePaths, progress, forceReupload: false, cancellationToken);

    /// <summary>
    /// B42 overload: as above, plus a <paramref name="forceReupload"/>
    /// flag that bypasses the metadata-skip fast path and the per-chunk
    /// dedup filter for every file in <paramref name="filePaths"/>.
    /// Used by the integrity-check auto-repair path, the manual Repair
    /// command, and the Force Full Scan UI button. Production hot paths
    /// must keep this <c>false</c>.
    /// </summary>
    public async Task BackupFilesAsync(
        IList<string> filePaths,
        IProgress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)>? progress,
        bool forceReupload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var opStopwatch = Stopwatch.StartNew();
        var crcFailStart = _blobService.TotalCrcFailures;
        var crcRetryStart = _blobService.TotalCrcRetries;

        var config = _databaseService.GetConfiguration();
        using var memoryBudget = MemoryBudget.FromConfig(config, CdcBufferOverhead);
        // B37: pool lives at operation scope; reporter reads its
        // residency in each emitted line.
        // B52: cap the pool's cached residency at 25% of the
        // configured budget (see ComputePoolCapBytes).
        // B72 (W5 Phase 4): attribute the pool's cached retention to
        // the same MemoryBudget so the cached bytes show up inside
        // PeakUsedBytes (see MirrorSyncToAzureAsync for the full
        // rationale; same pattern here).
        using var largeChunkPool = new ChunkBufferPool(
            ChunkBufferPool.LargeChunkBucketSizes,
            ComputePoolCapBytes(memoryBudget),
            memoryBudget);
        // B69 (W5 Phase 3 Commit 1): operation-scoped small-chunk
        // recycler that replaces the per-core ArrayPool<byte>.Shared
        // tier caches on the producer-side small-chunk path. See
        // ComputeSmallPoolCapBytes for the per-budget sizing.
        // B72: pool retention is now charged against the budget.
        using var smallChunkPool = new ChunkBufferPool(
            ChunkBufferPool.SmallChunkBucketSizes,
            ComputeSmallPoolCapBytes(memoryBudget),
            memoryBudget);
        // B36: see MirrorSyncToAzureAsync for the rationale; same pattern
        // here so any backup operation -- not just mirror -- gets the
        // periodic memory snapshot in the always-visible log pane.
        using var memReporter = new BackupMemoryReporter(
            memoryBudget,
            opLabel: "backup",
            emit: line => StatusChanged?.Invoke(this, line),
            interval: MemoryReporterIntervalOverride,
            largeChunkPool: largeChunkPool,
            smallChunkPool: smallChunkPool);

        // B54: clamp file-level fan-out against the active budget so a
        // small MemoryLimitMB does not admit more files than the budget
        // can sustain. Snapshot once per operation so the value cannot
        // drift mid-flight.
        var effectiveFileConcurrency =
            ComputeEffectiveFileConcurrency(memoryBudget, EffectiveMaxParallelFileBackups);

        Log($"BackupFilesAsync: Starting parallel backup of {filePaths.Count} files " +
            $"(max {effectiveFileConcurrency} concurrent, " +
            $"memoryBudget={(!memoryBudget.IsUnlimited ? $"{config.MemoryLimitMB} MB" : "unlimited")})");

        Metrics?.RecordContext("backup", config.MemoryLimitEnabled ? config.MemoryLimitMB : 0, config.MemoryLimitEnabled);

        // Decision-point record: justifies the chosen file-level concurrency
        // for this op so a post-hoc throughput comparison between two runs
        // can attribute a delta to the choice without re-deriving it from
        // process counters. Includes the inputs (file count, memory budget)
        // and the constants (MaxParallelFileBackups) that drove it.
        Metrics?.RecordDecision("backup-concurrency", new Dictionary<string, object?>
        {
            ["files"] = filePaths.Count,
            ["maxParallelFileBackups"] = EffectiveMaxParallelFileBackups,
            ["effectiveFileConcurrency"] = effectiveFileConcurrency,
            ["memoryBudgetMb"] = memoryBudget.IsUnlimited ? "unlimited" : (memoryBudget.TotalBytes / (1024 * 1024)).ToString(),
            ["memoryBudgetEnabled"] = config.MemoryLimitEnabled,
            ["processors"] = Environment.ProcessorCount
        });

        int completed = 0, failed = 0;
        long processedBytes = 0;
        try
        {
            (completed, failed, processedBytes) = await BackupFilesCoreAsync(
                filePaths, memoryBudget, largeChunkPool, smallChunkPool, progress, forceReupload,
                effectiveFileConcurrency, cancellationToken);
        }
        catch (Exception ex) when (TryExtractAuthFailure(ex, out var auth))
        {
            InvalidateAzureCredential(auth!);
            throw;
        }

        StatusChanged?.Invoke(this, $"Backup complete: {filePaths.Count} files processed");
        Log($"BackupFilesAsync: Complete - {filePaths.Count} files processed, {processedBytes} bytes");

        opStopwatch.Stop();
        var opElapsed = opStopwatch.Elapsed.TotalSeconds;
        Metrics?.RecordOperationAndFlush(new OperationMetrics
        {
            Operation = "backup",
            Files = filePaths.Count,
            Succeeded = completed,
            Failed = failed,
            Bytes = processedBytes,
            ElapsedSeconds = opElapsed,
            ThroughputMBps = opElapsed > 0 ? processedBytes / opElapsed / (1024 * 1024) : 0,
            FileConcurrency = effectiveFileConcurrency,
            MemoryBudgetMb = memoryBudget.IsUnlimited ? 0 : (int)(memoryBudget.TotalBytes / (1024 * 1024)),
            CrcFailCount = (int)(_blobService.TotalCrcFailures - crcFailStart),
            CrcRetryCount = (int)(_blobService.TotalCrcRetries - crcRetryStart)
        });

        // Decision-point: emit ONLY when the memory budget actually throttled.
        // See matching block in RestoreService.RestoreFilesWithRemappingAsync.
        if (memoryBudget.StallCount > 0)
        {
            Metrics?.RecordDecision("memory-budget-clamp", new Dictionary<string, object?>
            {
                ["operation"] = "backup",
                ["stallCount"] = memoryBudget.StallCount,
                ["budgetMb"] = memoryBudget.TotalBytes / (1024 * 1024),
                ["files"] = filePaths.Count,
                ["bytes"] = processedBytes
            });
        }
    }

    /// <summary>
    /// Core parallel backup logic shared by <see cref="BackupFilesAsync"/> and
    /// <see cref="MirrorSyncToAzureAsync"/>. Backs up files using
    /// <paramref name="effectiveFileConcurrency"/> concurrent workers with a
    /// shared <see cref="MemoryBudget"/>. Does NOT record operation-level
    /// metrics — callers are responsible.
    /// </summary>
    /// <param name="effectiveFileConcurrency">
    /// B54: budget-clamped file-level fan-out. Callers compute this once per
    /// operation via <see cref="ComputeEffectiveFileConcurrency"/> so the
    /// loop, the metrics, and the log line all agree on the same value.
    /// </param>
    /// <returns>Tuple of (completedFiles, failedFiles, totalBytesProcessed).</returns>
    private async Task<(int completed, int failed, long processedBytes)> BackupFilesCoreAsync(
        IList<string> filePaths,
        MemoryBudget memoryBudget,
        ChunkBufferPool largeChunkPool,
        ChunkBufferPool smallChunkPool,
        IProgress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)>? progress,
        bool forceReupload,
        int effectiveFileConcurrency,
        CancellationToken cancellationToken = default)
    {
        var totalFiles = filePaths.Count;
        long totalBytes = 0;
        // Phase 6 / discovered-#2: pad the two hot atomic counters onto their
        // own cache lines so MaxParallelFileBackups workers do not false-share
        // when concurrently updating processedBytes and completedFiles.
        PaddedLong processedBytes = default;
        PaddedLong completedFiles = default;

        // Calculate total bytes
        foreach (var filePath in filePaths)
        {
            try
            {
                FileInfo fileInfo = new(filePath);
                if (fileInfo.Exists)
                    totalBytes += fileInfo.Length;
            }
            catch
            {
                // Skip files we can't access
            }
        }

        await Parallel.ForEachAsync(
            // B19: project to (filePath, fileIndex) so each parallel worker
            // has a STABLE per-file identifier independent of completion
            // order. Pre-B19 we passed (int)completedFiles.Read() as the
            // fileIndex, which all 8 in-flight workers saw as the same
            // value at the moment they fired their first progress callback
            // -- the UI's startedFiles.TryAdd then suppressed every file
            // after the first, and FindActiveFile(0) updated the wrong row
            // when later files reported byte progress. The fix is the
            // smallest possible: hand each file its index in the input
            // list ONCE at iteration time and let it carry that index for
            // its entire lifetime.
            filePaths.Select((path, idx) => (path, idx)),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = effectiveFileConcurrency,
                CancellationToken = cancellationToken
            },
            async (item, ct) =>
            {
                var filePath = item.path;
                var fileIndex = item.idx;
                var fileName = Path.GetFileName(filePath);

                try
                {
                    FileInfo fileInfo = new(filePath);
                    if (!fileInfo.Exists)
                    {
                        Log($"BackupFilesCoreAsync: File not found, skipping: {filePath}");
                        return;
                    }

                    var currentFileSize = fileInfo.Length;
                    StatusChanged?.Invoke(this, $"Backing up: {fileName}");

                    // Track per-file byte deltas for accurate aggregate progress
                    long lastReportedFileBytes = 0;

                    Progress<(long current, long total)> fileProgress = new(p =>
                    {
                        var delta = p.current - Interlocked.Exchange(ref lastReportedFileBytes, p.current);
                        if (delta > 0)
                            processedBytes.Add(delta);

                        // B19: emit the file's own STABLE index as fileIndex.
                        // The first tuple element used to be the running
                        // count of completed files (a UI-counter value that
                        // collided across workers); now it's the per-file
                        // identity the UI keys ActiveFiles rows on.
                        progress?.Report((
                            fileIndex, totalFiles, fileName,
                            processedBytes.Read(), totalBytes,
                            p.current, currentFileSize));
                    });

                    var success = await BackupFileAsync(filePath, fileProgress, memoryBudget, largeChunkPool, smallChunkPool, forceReupload, ct);

                    if (success)
                    {
                        // Reconcile any remaining bytes not yet reported by progress callbacks
                        var finalReported = Interlocked.Exchange(ref lastReportedFileBytes, currentFileSize);
                        var remaining = currentFileSize - finalReported;
                        if (remaining > 0)
                            processedBytes.Add(remaining);

                        var done = completedFiles.Increment();
                        Log($"BackupFilesCoreAsync: [{done}/{totalFiles}] Successfully backed up: {fileName}");
                        _databaseService.RemovePendingChange(filePath);

                        // B19: the FINAL completion report also carries the
                        // file's own stable index so the UI can match it
                        // against the FileStarted row instead of guessing.
                        progress?.Report((
                            fileIndex, totalFiles, fileName,
                            processedBytes.Read(), totalBytes,
                            currentFileSize, currentFileSize));
                    }
                    else
                    {
                        Log($"BackupFilesCoreAsync: Failed to backup: {fileName}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"BackupFilesCoreAsync: Error backing up {filePath}: {ex.Message}");
                    ErrorOccurred?.Invoke(this, $"Failed to backup {fileName}: {ex.Message}");
                }
            });

        var completedSnapshot = (int)completedFiles.Read();
        return (completedSnapshot, totalFiles - completedSnapshot, processedBytes.Read());
    }

    /// <summary>
    /// Classifies a single local file for a backup preview by comparing it against
    /// the local DB record and optional Azure file list.
    /// Shared by <see cref="PreviewBackupSyncAsync"/> and <see cref="PreviewBackupFilesAsync"/>.
    /// </summary>
    /// <returns>The preview action, or null if the file doesn't exist or an error occurs.</returns>
    private PreviewFileAction? ClassifyFileForBackupPreview(
        string filePath,
        ISet<string>? azureFilePaths,
        string caller)
    {
        FileInfo fileInfo = new(filePath);
        if (!fileInfo.Exists) return null;

        var existingBackup = _databaseService.GetBackedUpFile(filePath);

        // If we have Azure file list, verify the file actually exists in Azure.
        // This prevents showing "overwrite" for files that were deleted from Azure.
        var actuallyExistsInAzure = azureFilePaths == null || azureFilePaths.Contains(filePath);

        if (existingBackup != null && !actuallyExistsInAzure)
        {
            Log($"{caller}: {Path.GetFileName(filePath)} - local DB has record but not in Azure, treating as new");
            existingBackup = null;
        }

        // Common fields are identical for every outcome — only Action and Reason vary.
        var result = new PreviewFileAction
        {
            FilePath = filePath,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTime
        };

        if (existingBackup == null)
        {
            result.Action = FileActionType.Create;
            result.Reason = "New file - never backed up";
        }
        else if (existingBackup.Status == BackupStatus.Completed)
        {
            if (fileInfo.LastWriteTimeUtc > existingBackup.LastModified ||
                fileInfo.Length != existingBackup.FileSize)
            {
                result.Action = FileActionType.Update;
                result.Reason = fileInfo.Length != existingBackup.FileSize
                    ? $"Size changed ({existingBackup.FileSize} → {fileInfo.Length})"
                    : "Modified since last backup";
            }
            else
            {
                result.Action = FileActionType.Skip;
                result.Reason = "Already backed up and unchanged";
            }
        }
        else if (existingBackup.Status == BackupStatus.Failed)
        {
            result.Action = FileActionType.Create;
            result.Reason = "Retrying previously failed backup";
        }
        else
        {
            result.Action = FileActionType.Skip;
            result.Reason = "Excluded or in-progress";
        }

        return result;
    }
}
