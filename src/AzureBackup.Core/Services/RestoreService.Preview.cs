using System.Collections.Concurrent;
using AzureBackup.Core.Models;

namespace AzureBackup.Core.Services;

/// <summary>
/// Preview generation methods for restore, delete, and mirror sync operations.
/// </summary>
public partial class RestoreService
{
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
}
