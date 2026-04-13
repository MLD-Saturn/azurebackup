using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Scan, sync, mirror, preview, and batch backup operations.
/// </summary>
public partial class BackupOrchestrator
{
    /// <summary>
    /// Performs a full scan and backup of all watched folders.
    /// Queues ALL files found regardless of their current backup status.
    /// Use PerformInitialSyncAsync for smarter syncing that skips already-backed-up files.
    /// </summary>
    public async Task PerformFullScanAsync(IProgress<(int current, int total, string file)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = _databaseService.GetConfiguration();
        List<string> allFiles = new();

        StatusChanged?.Invoke(this, "Scanning folders...");
        Log("PerformFullScanAsync: Starting full scan of all watched folders");

        // Scan all watched folders
        foreach (var folder in config.WatchedFolders.Where(f => f.IsEnabled))
        {
            var files = await _fileWatcherService.ScanFolderAsync(folder, cancellationToken);
            allFiles.AddRange(files);
            Log($"PerformFullScanAsync: Found {files.Count} files in {folder.Path}");
        }

        StatusChanged?.Invoke(this, $"Found {allFiles.Count} files to process");

        // Queue all files for backup
        for (var i = 0; i < allFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var file = allFiles[i];
            progress?.Report((i + 1, allFiles.Count, file));

            _databaseService.QueueFileChange(new FileChangeEvent
            {
                FilePath = file,
                ChangeType = FileChangeType.Created,
                DetectedAt = DateTime.UtcNow
            });
        }

        StatusChanged?.Invoke(this, $"Queued {allFiles.Count} files for backup");
        Log($"PerformFullScanAsync: Complete - queued {allFiles.Count} files");
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
                    _databaseService.QueueFileChange(new FileChangeEvent
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
                        _databaseService.QueueFileChange(new FileChangeEvent
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
                    _databaseService.QueueFileChange(new FileChangeEvent
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

        StatusChanged?.Invoke(this, $"Mirror sync: scanning {localFolder.Path}");

        // Phase 1: Scan local folder for files
        var localFiles = await _fileWatcherService.ScanFolderAsync(localFolder, cancellationToken);
        HashSet<string> localFilePaths = new(localFiles, StringComparer.OrdinalIgnoreCase);

        Log($"MirrorSyncToAzureAsync: Found {localFiles.Count} local files");

        // Phase 2: Get existing backups for this folder
        var existingBackups = _databaseService.GetAllBackedUpFiles()
            .Where(f => f.LocalPath.StartsWith(localFolder.Path, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f.LocalPath, StringComparer.OrdinalIgnoreCase);

        var totalOperations = localFiles.Count + existingBackups.Count;
        var currentOp = 0;

        // Phase 3: Backup new and modified files
        foreach (var localFilePath in localFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentOp++;

            var fileName = Path.GetFileName(localFilePath);

            try
            {
                if (existingBackups.TryGetValue(localFilePath, out var existingBackup))
                {
                    // File exists in backup - check if modified
                    FileInfo fileInfo = new(localFilePath);
                    
                    if (fileInfo.Length == existingBackup.FileSize &&
                        Math.Abs((fileInfo.LastWriteTimeUtc - existingBackup.LastModified).TotalSeconds) < 2)
                    {
                        // Quick check suggests file is unchanged
                        result.FilesUnchanged++;
                        progress?.Report((currentOp, totalOperations, fileName, "Unchanged"));
                        continue;
                    }
                }

                // File is new or modified - backup it
                progress?.Report((currentOp, totalOperations, fileName, "Backing up"));
                var success = await BackupFileAsync(localFilePath, cancellationToken);

                if (success)
                {
                    result.FilesTransferred++;
                    FileInfo fileInfo = new(localFilePath);
                    result.BytesTransferred += fileInfo.Length;
                }
                else
                {
                    result.FilesErrored++;
                    result.Errors.Add($"Failed to backup: {localFilePath}");
                }
            }
            catch (Exception ex)
            {
                result.FilesErrored++;
                result.Errors.Add($"Error backing up {localFilePath}: {ex.Message}");
                Log($"MirrorSyncToAzureAsync: Error backing up {localFilePath}: {ex.Message}");
            }
        }

        // Phase 4: Mark deleted files (files in backup but not locally)
        foreach (var (backupPath, backupFile) in existingBackups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            currentOp++;

            if (!localFilePaths.Contains(backupPath))
            {
                try
                {
                    progress?.Report((currentOp, totalOperations, Path.GetFileName(backupPath), "Marking deleted"));
                    
                    // Remove chunk references (this will delete orphaned chunks immediately)
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
                    
                    // Mark as excluded (deleted) but keep in Azure for potential restore
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
    /// <param name="progress">Progress reporter with file index, total files, file name, overall bytes, total bytes, current file bytes, current file size</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task BackupFilesAsync(
        IList<string> filePaths,
        IProgress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        // Create a shared memory budget from the user's config.
        // All concurrent file backups share this single budget so the total
        // in-flight chunk memory stays within the user's limit.
        var config = _databaseService.GetConfiguration();
        using var memoryBudget = MemoryBudget.FromConfig(config, CdcBufferOverhead);

        Log($"BackupFilesAsync: Starting parallel backup of {filePaths.Count} files " +
            $"(max {MaxParallelFileBackups} concurrent, " +
            $"memoryBudget={(!memoryBudget.IsUnlimited ? $"{config.MemoryLimitMB} MB" : "unlimited")})");

        var totalFiles = filePaths.Count;
        long totalBytes = 0;
        long processedBytes = 0;
        int completedFiles = 0;

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
            filePaths,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelFileBackups,
                CancellationToken = cancellationToken
            },
            async (filePath, ct) =>
            {
                var fileName = Path.GetFileName(filePath);

                try
                {
                    FileInfo fileInfo = new(filePath);
                    if (!fileInfo.Exists)
                    {
                        Log($"BackupFilesAsync: File not found, skipping: {filePath}");
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
                            Interlocked.Add(ref processedBytes, delta);

                        progress?.Report((
                            Volatile.Read(ref completedFiles), totalFiles, fileName,
                            Interlocked.Read(ref processedBytes), totalBytes,
                            p.current, currentFileSize));
                    });

                    var success = await BackupFileAsync(filePath, fileProgress, memoryBudget, ct);

                    if (success)
                    {
                        // Reconcile any remaining bytes not yet reported by progress callbacks
                        var finalReported = Interlocked.Exchange(ref lastReportedFileBytes, currentFileSize);
                        var remaining = currentFileSize - finalReported;
                        if (remaining > 0)
                            Interlocked.Add(ref processedBytes, remaining);

                        var done = Interlocked.Increment(ref completedFiles);
                        Log($"BackupFilesAsync: [{done}/{totalFiles}] Successfully backed up: {fileName}");
                        _databaseService.RemovePendingChange(filePath);

                        progress?.Report((
                            done, totalFiles, fileName,
                            Interlocked.Read(ref processedBytes), totalBytes,
                            currentFileSize, currentFileSize));
                    }
                    else
                    {
                        Log($"BackupFilesAsync: Failed to backup: {fileName}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"BackupFilesAsync: Error backing up {filePath}: {ex.Message}");
                    ErrorOccurred?.Invoke(this, $"Failed to backup {fileName}: {ex.Message}");
                }
            });

        StatusChanged?.Invoke(this, $"Backup complete: {totalFiles} files processed");
        Log($"BackupFilesAsync: Complete - {totalFiles} files processed, {Interlocked.Read(ref processedBytes)} bytes");
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

        if (existingBackup == null)
        {
            return new PreviewFileAction
            {
                FilePath = filePath,
                FileSize = fileInfo.Length,
                LastModified = fileInfo.LastWriteTime,
                Action = FileActionType.Create,
                Reason = "New file - never backed up"
            };
        }

        if (existingBackup.Status == BackupStatus.Completed)
        {
            if (fileInfo.LastWriteTimeUtc > existingBackup.LastModified ||
                fileInfo.Length != existingBackup.FileSize)
            {
                return new PreviewFileAction
                {
                    FilePath = filePath,
                    FileSize = fileInfo.Length,
                    LastModified = fileInfo.LastWriteTime,
                    Action = FileActionType.Update,
                    Reason = fileInfo.Length != existingBackup.FileSize
                        ? $"Size changed ({existingBackup.FileSize} → {fileInfo.Length})"
                        : "Modified since last backup"
                };
            }

            return new PreviewFileAction
            {
                FilePath = filePath,
                FileSize = fileInfo.Length,
                LastModified = fileInfo.LastWriteTime,
                Action = FileActionType.Skip,
                Reason = "Already backed up and unchanged"
            };
        }

        if (existingBackup.Status == BackupStatus.Failed)
        {
            return new PreviewFileAction
            {
                FilePath = filePath,
                FileSize = fileInfo.Length,
                LastModified = fileInfo.LastWriteTime,
                Action = FileActionType.Create,
                Reason = "Retrying previously failed backup"
            };
        }

        return new PreviewFileAction
        {
            FilePath = filePath,
            FileSize = fileInfo.Length,
            LastModified = fileInfo.LastWriteTime,
            Action = FileActionType.Skip,
            Reason = "Excluded or in-progress"
        };
    }
}
