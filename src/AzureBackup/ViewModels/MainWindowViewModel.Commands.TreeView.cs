using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// Tree view and folder management commands for MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{
    #region Tree View Commands

    [RelayCommand]
    private void ToggleViewMode()
    {
        UseTreeView = !UseTreeView;
        AddLog(UseTreeView ? "Switched to tree view" : "Switched to flat list view");
        
        if (UseTreeView)
        {
            BuildFileTree();
        }
    }


    [RelayCommand]
    private void ExpandAllTreeNodes()
    {
        // Expand Azure file tree
        foreach (var root in FileTreeRoots)
        {
            root.ExpandAll();
        }
        
        // Expand local file tree
        foreach (var root in LocalFileTreeRoots)
        {
            root.ExpandAll();
        }
    }

    [RelayCommand]
    private void CollapseAllTreeNodes()
    {
        // Collapse Azure file tree
        foreach (var root in FileTreeRoots)
        {
            root.CollapseAll();
        }
        
        // Collapse local file tree
        foreach (var root in LocalFileTreeRoots)
        {
            root.CollapseAll();
        }
    }

    [RelayCommand]
    private void SetCustomRestorePath()
    {
        if (SelectedTreeNode == null || !SelectedTreeNode.IsFolder)
        {
            AddLog("Please select a folder to set custom restore path");
            return;
        }

        if (string.IsNullOrWhiteSpace(CustomRestoreBasePath))
        {
            AddLog("Please enter a custom restore path");
            return;
        }

        SelectedTreeNode.SetCustomRestorePathAndNotify(CustomRestoreBasePath);
        AddLog($"Set restore path for '{SelectedTreeNode.Name}': {SelectedTreeNode.FullPath} ? {CustomRestoreBasePath}");
    }

    [RelayCommand]
    private void ClearCustomRestorePath()
    {
        if (SelectedTreeNode == null)
            return;

        SelectedTreeNode.ClearCustomRestorePathRecursive();
        AddLog($"Cleared custom restore path for '{SelectedTreeNode.Name}'");
    }

    [RelayCommand]
    private void BrowseRemapFolder()
    {
        // Request the View to open a folder picker dialog for path remapping
        RemapFolderPickerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the View after a remap folder is selected from the picker.
    /// </summary>
    public void SetRemapFolderPath(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;
        
        CustomRestoreBasePath = folderPath;
        
        // Auto-apply the path if a folder is selected
        if (SelectedTreeNode?.IsFolder == true)
        {
            SelectedTreeNode.SetCustomRestorePathAndNotify(folderPath);
            AddLog($"Set restore path for '{SelectedTreeNode.Name}': {SelectedTreeNode.FullPath} ? {folderPath}");
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedTreeFilesAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first");
            return;
        }

        Program.Logger?.Log("RestoreSelectedTreeFilesAsync: Starting tree view restore");

        // Get selected files from tree
        var selectedFiles = FileTreeRoots
            .SelectMany(r => r.GetSelectedFiles())
            .ToList();

        if (selectedFiles.Count == 0)
        {
            AddLog("Please select files to restore");
            return;
        }

        // Build list of (file, target path) pairs
        var filesWithPaths = selectedFiles
            .Where(f => f.File != null)
            .Select(f => (
                file: f.File!, 
                targetPath: RestoreToOriginalLocation 
                    ? f.File!.LocalPath
                    : (!string.IsNullOrWhiteSpace(RestoreDirectory) 
                        ? Path.Combine(RestoreDirectory, Path.GetFileName(f.File!.LocalPath))
                        : f.EffectiveRestorePath)))
            .ToList();

        Program.Logger?.Log($"RestoreSelectedTreeFilesAsync: {filesWithPaths.Count} files, totalBytes={filesWithPaths.Sum(f => f.file.FileSize)}");

        // Generate preview on background thread to avoid UI freeze for large file counts
        // (File.Exists + FileInfo for 19K files can take seconds on HDD)
        AddLog($"Preparing preview for {filesWithPaths.Count} files...");
        var preview = await Task.Run(() => _restoreService.PreviewRestoreWithRemapping(filesWithPaths));

        var confirmed = await ShowPreviewDialogAsync(preview);

        if (!confirmed)
        {
            AddLog("Restore operation cancelled by user");
            return;
        }

        // Remove files the user excluded in the preview dialog
        var excluded = preview.ExcludedFilePaths;
        if (excluded.Count > 0)
        {
            filesWithPaths = filesWithPaths
                .Where(f => !excluded.Contains(f.file.LocalPath))
                .ToList();
            Program.Logger?.Log($"RestoreSelectedTreeFilesAsync: {excluded.Count} files excluded by user, {filesWithPaths.Count} remaining");
        }

        if (filesWithPaths.Count == 0)
        {
            AddLog("All files were excluded - nothing to restore");
            return;
        }

        IsOperationInProgress = true;
        CreateOperationCts();

        try
        {
            AddLog($"Restoring {filesWithPaths.Count} files with path remapping...");
            Program.Logger?.Log($"RestoreSelectedTreeFilesAsync: Calling RestoreFilesWithRemappingAsync");

            var result = await ExecuteRestoreWithRemappingAsync(filesWithPaths, _operationCts!.Token);

            LogRestoreResult(result);
            Program.Logger?.Log($"RestoreSelectedTreeFilesAsync: Complete - {result.SuccessfulFiles.Count} OK, " +
                $"{result.CorruptedRecoveryFiles.Count} corrupted-recovered, {result.FailedFiles.Count} failed");

            // Refresh local file panel so newly restored files appear immediately
            await RefreshLocalFilesAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog("Restore cancelled");
            Program.Logger?.Log("RestoreSelectedTreeFilesAsync: Cancelled by user");
            ProgressTab.MarkCancelled();
        }
        catch (Exception ex)
        {
            AddLog($"Restore failed: {ex.Message}");
            Program.Logger?.LogException(ex, "RestoreSelectedTreeFilesAsync");
        }
        finally
        {
            IsOperationInProgress = false;
            StopProgressTracking();
        }
    }

    [RelayCommand]
    private async Task MirrorSyncToLocalAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please unlock first");
            return;
        }

        if (SelectedTreeNode == null || !SelectedTreeNode.IsFolder)
        {
            AddLog("Please select a folder to sync");
            return;
        }

        if (string.IsNullOrWhiteSpace(CustomRestoreBasePath))
        {
            AddLog("Please enter a target directory for mirror sync");
            return;
        }

        // Validate target path
        try
        {
            var normalizedPath = Path.GetFullPath(CustomRestoreBasePath);
            if (!Path.IsPathRooted(normalizedPath))
            {
                AddLog("Please enter an absolute path for the target directory");
                return;
            }
        }
        catch (Exception ex)
        {
            AddLog($"Invalid target path: {ex.Message}");
            return;
        }

        var sourceFolder = SelectedTreeNode.FullPath;
        var targetFolder = CustomRestoreBasePath;

        // Get all files under the selected folder
        var filesToSync = SelectedTreeNode.GetAllDescendants()
            .Where(n => n.IsFile && n.File != null)
            .Select(n => n.File!)
            .ToList();

        // Generate preview
        AddLog("Analyzing sync operation...");
        IsOperationInProgress = true;
        
        try
        {
            var preview = await _restoreService.PreviewMirrorSyncAsync(
                filesToSync, targetFolder, sourceFolder);

            // Show preview dialog and get user confirmation
            IsOperationInProgress = false;
            var confirmed = await ShowPreviewDialogAsync(preview);
            
            if (!confirmed)
            {
                AddLog("Mirror sync cancelled by user");
                return;
            }

            if (!preview.HasChanges)
            {
                AddLog("No changes needed - all files are up to date");
                return;
            }

            // Proceed with sync
            IsOperationInProgress = true;
            CreateOperationCts();

            AddLog($"Mirror sync: {sourceFolder} → {targetFolder}");

            var totalBytes = filesToSync.Sum(f => f.FileSize);
            const long SmallFileThreshold = 100L * 1024 * 1024;
            var smallFileCount = filesToSync.Count(f => f.FileSize <= SmallFileThreshold);
            var smallFileTotalBytes = filesToSync.Where(f => f.FileSize <= SmallFileThreshold).Sum(f => f.FileSize);

            // Initialize progress tab
            StartProgressTab("Mirror sync", filesToSync.Count, totalBytes, smallFileCount, smallFileTotalBytes);
            StartProgressTracking("Mirror sync", filesToSync.Count, totalBytes);

            // Per-file byte tracking for ProgressTab.
            // perFileBytes stores the last reported bytes per file for delta computation.
            // totalBytesProcessed is a running total updated via Interlocked.Add with deltas — O(1) per update.
            var perFileBytes = new long[filesToSync.Count];
            long totalBytesProcessed = 0;
            var startedFileSet = new ConcurrentDictionary<int, byte>();
            var completedFileSet = new ConcurrentDictionary<int, byte>();
            int mirrorSmallCompleted = 0;
            long mirrorSmallBytes = 0;

            // Pre-build filename → indices multimap for O(1) lookup in the "Unchanged" handler.
            // Multiple files can share a filename (e.g., README.md in different directories),
            // so each filename maps to a list of indices into filesToSync.
            var fileNameToIndices = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < filesToSync.Count; i++)
            {
                var fn = Path.GetFileName(filesToSync[i].LocalPath);
                if (!fileNameToIndices.TryGetValue(fn, out var indices))
                {
                    indices = [];
                    fileNameToIndices[fn] = indices;
                }
                indices.Add(i);
            }

            bool deletePhaseReported = false;

            Progress<(int current, int total, string file, string action)> progress = new(p =>
            {
                // Detect transition to delete phase and report to ProgressTab
                if (string.Equals(p.action, "Deleting", StringComparison.Ordinal) && !deletePhaseReported)
                {
                    deletePhaseReported = true;
                    ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                    {
                        Type = AzureBackup.Core.Models.OperationProgressType.PhaseChanged,
                        PhaseDescription = "Cleaning up extra local files..."
                    });
                }

                // When mirror sync reports a file as "Unchanged", it will never fire byte-level
                // progress for that file. Treat it as instantly complete so the overall progress
                // bar and file counts stay accurate.
                if (!string.Equals(p.action, "Unchanged", StringComparison.Ordinal))
                    return;

                // Look up candidate indices by filename, then claim the first uncompleted one.
                if (!fileNameToIndices.TryGetValue(p.file, out var candidateIndices))
                    return;

                foreach (var i in candidateIndices)
                {
                    if (!completedFileSet.TryAdd(i, 0))
                        continue;

                    // Add full file size to running total (unchanged files are instantly complete)
                    var totalProcessed = Interlocked.Add(ref totalBytesProcessed, filesToSync[i].FileSize);

                    var isSmall = filesToSync[i].FileSize <= SmallFileThreshold;
                    if (isSmall)
                    {
                        var sc = Interlocked.Increment(ref mirrorSmallCompleted);
                        var sb = Interlocked.Add(ref mirrorSmallBytes, filesToSync[i].FileSize);
                        ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                        {
                            Type = AzureBackup.Core.Models.OperationProgressType.SmallFileGroupProgress,
                            SmallFilesCompleted = sc, SmallFilesTotal = smallFileCount,
                            SmallFilesBytesProcessed = sb, SmallFilesTotalBytes = smallFileTotalBytes,
                            TotalBytesProcessed = totalProcessed, TotalBytes = totalBytes,
                            TotalFilesCompleted = completedFileSet.Count, TotalFiles = filesToSync.Count
                        });
                    }
                    else
                    {
                        var fn = Path.GetFileName(filesToSync[i].LocalPath);
                        ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                        {
                            Type = AzureBackup.Core.Models.OperationProgressType.FileCompleted,
                            FileIndex = i,
                            FileName = fn,
                            FileSize = filesToSync[i].FileSize,
                            FileBytesProcessed = filesToSync[i].FileSize,
                            FileStatus = AzureBackup.Core.Models.FileOperationStatus.Complete,
                            TotalBytesProcessed = totalProcessed, TotalBytes = totalBytes,
                            TotalFilesCompleted = completedFileSet.Count, TotalFiles = filesToSync.Count
                        });
                    }
                    break;
                }
            });

            Progress<(long bytesCompleted, long fileSize, int fileIndex)> byteProgress = new(p =>
            {
                var fileName = Path.GetFileName(filesToSync[p.fileIndex].LocalPath);
                var isSmall = filesToSync[p.fileIndex].FileSize <= SmallFileThreshold;

                if (startedFileSet.TryAdd(p.fileIndex, 0) && !isSmall)
                {
                    ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                    {
                        Type = AzureBackup.Core.Models.OperationProgressType.FileStarted,
                        FileIndex = p.fileIndex, FileName = fileName, FileSize = p.fileSize,
                        FileStatus = AzureBackup.Core.Models.FileOperationStatus.Downloading,
                        TotalBytesProcessed = Volatile.Read(ref totalBytesProcessed), TotalBytes = totalBytes,
                        TotalFilesCompleted = completedFileSet.Count, TotalFiles = filesToSync.Count
                    });
                }

                // Compute delta from previous value and add to running total — O(1) instead of O(n) scan
                var previousBytes = Interlocked.Exchange(ref perFileBytes[p.fileIndex], p.bytesCompleted);
                var delta = p.bytesCompleted - previousBytes;
                var totalProcessed = Interlocked.Add(ref totalBytesProcessed, delta);

                if (!isSmall)
                {
                    ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                    {
                        Type = AzureBackup.Core.Models.OperationProgressType.FileProgress,
                        FileIndex = p.fileIndex, FileName = fileName, FileSize = p.fileSize,
                        FileBytesProcessed = p.bytesCompleted,
                        FileStatus = AzureBackup.Core.Models.FileOperationStatus.Downloading,
                        TotalBytesProcessed = totalProcessed, TotalBytes = totalBytes,
                        TotalFilesCompleted = completedFileSet.Count, TotalFiles = filesToSync.Count
                    });
                }

                if (p.bytesCompleted >= p.fileSize && completedFileSet.TryAdd(p.fileIndex, 0))
                {
                    if (isSmall)
                    {
                        var sc = Interlocked.Increment(ref mirrorSmallCompleted);
                        var sb = Interlocked.Add(ref mirrorSmallBytes, p.fileSize);
                        ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                        {
                            Type = AzureBackup.Core.Models.OperationProgressType.SmallFileGroupProgress,
                            SmallFilesCompleted = sc, SmallFilesTotal = smallFileCount,
                            SmallFilesBytesProcessed = sb, SmallFilesTotalBytes = smallFileTotalBytes,
                            TotalBytesProcessed = totalProcessed, TotalBytes = totalBytes,
                            TotalFilesCompleted = completedFileSet.Count, TotalFiles = filesToSync.Count
                        });
                    }
                    else
                    {
                        ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                        {
                            Type = AzureBackup.Core.Models.OperationProgressType.FileCompleted,
                            FileIndex = p.fileIndex, FileName = fileName, FileSize = p.fileSize,
                            FileBytesProcessed = p.fileSize,
                            FileStatus = AzureBackup.Core.Models.FileOperationStatus.Complete,
                            TotalBytesProcessed = totalProcessed, TotalBytes = totalBytes,
                            TotalFilesCompleted = completedFileSet.Count, TotalFiles = filesToSync.Count
                        });
                    }
                }
            });

            var result = await _restoreService.MirrorSyncToLocalAsync(
                filesToSync,
                targetFolder,
                sourceFolder,
                progress,
                byteProgress,
                _operationCts!.Token);

            // Show completion summary
            ProgressTab.CompleteOperation(
                result.FilesTransferred, result.FilesErrored,
                result.FilesCorruptedRecovered, result.BytesTransferred);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => CurrentView = "Progress");

            AddLog($"Mirror sync complete: {result.FilesTransferred} restored, {result.FilesDeleted} deleted, " +
                   $"{result.FilesUnchanged} unchanged, {result.FilesErrored} errors");

            if (result.FilesCorruptedRecovered > 0)
            {
                AddLog($"  {result.FilesCorruptedRecovered} file(s) recovered to __corrupted__ subfolder");
            }

            foreach (var error in result.Errors.Take(5))
            {
                AddLog($"  Error: {error}");
            }
            if (result.Errors.Count > 5)
            {
                AddLog($"  ... and {result.Errors.Count - 5} more errors");
            }

            // Refresh local file panel so newly synced files appear immediately
            await RefreshLocalFilesAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog("Mirror sync cancelled");
            ProgressTab.MarkCancelled();
        }
        catch (Exception ex)
        {
            AddLog($"Mirror sync failed: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
            StopProgressTracking();
        }
    }

    #region Restore Helpers

    /// <summary>
    /// Executes a batch restore with progress tracking on the Progress tab.
    /// Uses RestoreFilesWithRemappingAsync for parallel, corrupted-recovery-aware restore.
    /// Reports per-file start/progress/complete events to <see cref="ProgressTab"/>.
    /// Small files (≤100 MB) are grouped into an aggregate row instead of individual rows.
    /// </summary>
    private async Task<AzureBackup.Core.Services.RestoreResult> ExecuteRestoreWithRemappingAsync(
        List<(BackedUpFile file, string targetPath)> filesWithPaths,
        CancellationToken cancellationToken)
    {
        var totalBytes = filesWithPaths.Sum(f => f.file.FileSize);
        const long SmallFileThreshold = 100L * 1024 * 1024;
        var smallFileCount = filesWithPaths.Count(f => f.file.FileSize <= SmallFileThreshold);
        var smallFileTotalBytes = filesWithPaths.Where(f => f.file.FileSize <= SmallFileThreshold).Sum(f => f.file.FileSize);

        // Initialize progress tab and auto-switch to it
        StartProgressTab("Restoring", filesWithPaths.Count, totalBytes, smallFileCount, smallFileTotalBytes);

        // Also keep legacy tracking for tray tooltip and IsTransferInProgress
        StartProgressTracking("Restoring", filesWithPaths.Count, totalBytes);

        // Track per-file bytes atomically for parallel progress aggregation.
        // perFileBytes stores last reported bytes per file for delta computation.
        // totalBytesProcessed is a running total updated via Interlocked.Add — O(1) per update.
        var perFileBytes = new long[filesWithPaths.Count];
        long totalBytesProcessed = 0;
        var startedFileSet = new ConcurrentDictionary<int, byte>();
        var completedFileSet = new ConcurrentDictionary<int, byte>();
        int smallFilesCompleted = 0;
        long smallFilesBytesProcessed = 0;

        Progress<(int current, int total, string file)> fileProgress = new(_ => { });

        Progress<(long bytesCompleted, long fileSize, int fileIndex)> byteProgress = new(p =>
        {
            var fileName = Path.GetFileName(filesWithPaths[p.fileIndex].file.LocalPath);
            var isSmall = filesWithPaths[p.fileIndex].file.FileSize <= SmallFileThreshold;

            // Report file started on first progress for this file (large files only — small are grouped)
            if (startedFileSet.TryAdd(p.fileIndex, 0) && !isSmall)
            {
                ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                {
                    Type = AzureBackup.Core.Models.OperationProgressType.FileStarted,
                    FileIndex = p.fileIndex,
                    FileName = fileName,
                    FileSize = p.fileSize,
                    FileStatus = AzureBackup.Core.Models.FileOperationStatus.Downloading,
                    TotalBytesProcessed = Volatile.Read(ref totalBytesProcessed),
                    TotalBytes = totalBytes,
                    TotalFilesCompleted = completedFileSet.Count,
                    TotalFiles = filesWithPaths.Count
                });
            }

            // Compute delta from previous value and add to running total — O(1) instead of O(n) scan
            var previousBytes = Interlocked.Exchange(ref perFileBytes[p.fileIndex], p.bytesCompleted);
            var delta = p.bytesCompleted - previousBytes;
            var totalProcessed = Interlocked.Add(ref totalBytesProcessed, delta);

            // Report byte progress for large files
            if (!isSmall)
            {
                ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                {
                    Type = AzureBackup.Core.Models.OperationProgressType.FileProgress,
                    FileIndex = p.fileIndex,
                    FileName = fileName,
                    FileSize = p.fileSize,
                    FileBytesProcessed = p.bytesCompleted,
                    FileStatus = AzureBackup.Core.Models.FileOperationStatus.Downloading,
                    TotalBytesProcessed = totalProcessed,
                    TotalBytes = totalBytes,
                    TotalFilesCompleted = completedFileSet.Count,
                    TotalFiles = filesWithPaths.Count
                });
            }

            // Detect file completion
            if (p.bytesCompleted >= p.fileSize && completedFileSet.TryAdd(p.fileIndex, 0))
            {
                if (isSmall)
                {
                    var sc = Interlocked.Increment(ref smallFilesCompleted);
                    var sb = Interlocked.Add(ref smallFilesBytesProcessed, p.fileSize);
                    ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                    {
                        Type = AzureBackup.Core.Models.OperationProgressType.SmallFileGroupProgress,
                        SmallFilesCompleted = sc,
                        SmallFilesTotal = smallFileCount,
                        SmallFilesBytesProcessed = sb,
                        SmallFilesTotalBytes = smallFileTotalBytes,
                        TotalBytesProcessed = totalProcessed,
                        TotalBytes = totalBytes,
                        TotalFilesCompleted = completedFileSet.Count,
                        TotalFiles = filesWithPaths.Count
                    });
                }
                else
                {
                    ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                    {
                        Type = AzureBackup.Core.Models.OperationProgressType.FileCompleted,
                        FileIndex = p.fileIndex,
                        FileName = fileName,
                        FileSize = p.fileSize,
                        FileBytesProcessed = p.fileSize,
                        FileStatus = AzureBackup.Core.Models.FileOperationStatus.Complete,
                        TotalBytesProcessed = totalProcessed,
                        TotalBytes = totalBytes,
                        TotalFilesCompleted = completedFileSet.Count,
                        TotalFiles = filesWithPaths.Count
                    });
                }
            }
        });

        var result = await _restoreService.RestoreFilesWithRemappingAsync(
            filesWithPaths,
            overwriteExisting: true,
            fileProgress,
            byteProgress,
            cancellationToken);

        // Show completion summary on the progress tab
        ProgressTab.CompleteOperation(
            result.SuccessfulFiles.Count,
            result.FailedFiles.Count,
            result.CorruptedRecoveryFiles.Count,
            result.TotalBytesRestored);

        // Auto-switch to progress tab for completion summary
        Avalonia.Threading.Dispatcher.UIThread.Post(() => CurrentView = "Progress");

        return result;
    }

    /// <summary>
    /// Initializes the Progress tab and auto-switches to it.
    /// Shared by restore, backup, and mirror operations.
    /// </summary>
    private void StartProgressTab(string operationType, int totalFiles, long totalBytes, int smallFileCount = 0, long smallFileTotalBytes = 0)
    {
        _viewBeforeProgress = CurrentView;
        ShowProgressNavButton = true;
        ProgressTab.StartOperation(operationType, totalFiles, totalBytes, smallFileCount, smallFileTotalBytes);
        CurrentView = "Progress";
    }

    /// <summary>
    /// Logs a restore result with corrupted recovery details.
    /// </summary>
    private void LogRestoreResult(AzureBackup.Core.Services.RestoreResult result)
    {
        AddLog($"Restore complete: {result.SuccessfulFiles.Count} succeeded, {result.FailedFiles.Count} failed");

        if (result.CorruptedRecoveryFiles.Count > 0)
        {
            AddLog($"  {result.CorruptedRecoveryFiles.Count} file(s) recovered to __corrupted__ subfolder:");
            foreach (var (originalPath, recoveredPath, badChunks) in result.CorruptedRecoveryFiles)
            {
                var detail = badChunks > 0 ? $"{badChunks} chunk(s) zero-filled" : "data intact";
                AddLog($"    {Path.GetFileName(originalPath)} → {recoveredPath} ({detail})");
            }
        }
    }

    #endregion

    /// <summary>
    /// Builds the file tree from the flat restorable files list.
    /// </summary>
    private void BuildFileTree()
    {
        var files = RestorableFiles.Select(f => f.Model).ToList();
        var treeRoots = FileTreeNodeViewModel.BuildTree(files);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            FileTreeRoots.Clear();
            foreach (var root in treeRoots)
            {
                FileTreeRoots.Add(root);
            }
            OnPropertyChanged(nameof(ShowAzureEmptyState));
            
            // Ensure selection state is updated after tree rebuild
            NotifySelectionChanged();
        });
    }

    #endregion

    #region Folder Management Commands

    [RelayCommand]
    private void AddWatchedFolder()
    {
        // Request the View to open a folder picker dialog
        FolderPickerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the View after a folder is selected from the picker.
    /// </summary>
    public async void AddWatchedFolderPath(string folderPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            
            // Check if folder already exists in the list
            if (WatchedFolders.Any(f => f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase)))
            {
                AddLog($"Folder already in watch list: {folderPath}");
                return;
            }

            WatchedFolders.Add(new WatchedFolderViewModel(new WatchedFolder
            {
                Path = folderPath,
                IsEnabled = true,
                ExcludePatterns = []
            }));
            
            AddLog($"Added watch folder: {folderPath}");
            SaveSettings();
            
            // Refresh the local files tree to show the new folder
            await RefreshLocalFilesAsync();
        }
        catch (Exception ex)
        {
            AddLog($"Error adding watch folder: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Error adding watch folder: {ex}");
        }
    }

    [RelayCommand]
    private async Task RemoveWatchedFolderAsync()
    {
        if (SelectedWatchedFolder != null)
        {
            var folderPath = SelectedWatchedFolder.Path;
            WatchedFolders.Remove(SelectedWatchedFolder);
            SaveSettings();
            AddLog($"Removed watched folder: {folderPath}");
            
            // Refresh the local files tree to remove the folder
            await RefreshLocalFilesAsync();
        }
    }

    /// <summary>
    /// Gets whether a local folder can be removed (a root watched folder is selected or checked).
    /// </summary>
    public bool CanRemoveSelectedLocalFolder => 
        (SelectedLocalTreeNode != null && 
         SelectedLocalTreeNode.Parent == null && 
         SelectedLocalTreeNode.IsFolder) ||
        LocalFileTreeRoots.Any(r => r.IsSelected);

    [RelayCommand]
    private async Task RemoveSelectedLocalFolderAsync()
    {
        // First check for checked (checkbox) root folders
        var checkedRoots = LocalFileTreeRoots.Where(r => r.IsSelected).ToList();
        
        if (checkedRoots.Count > 0)
        {
            // Remove all checked root folders
            foreach (var root in checkedRoots)
            {
                var folderPath = root.FullPath;
                var folderToRemove = WatchedFolders.FirstOrDefault(f => 
                    f.Path.Equals(folderPath, StringComparison.OrdinalIgnoreCase));
                
                if (folderToRemove != null)
                {
                    WatchedFolders.Remove(folderToRemove);
                    AddLog($"Removed watched folder: {folderPath}");
                }
            }
            
            SaveSettings();
            await RefreshLocalFilesAsync();
            return;
        }
        
        // Fall back to selected (clicked) tree node
        if (SelectedLocalTreeNode == null || SelectedLocalTreeNode.Parent != null)
        {
            AddLog("Please select or check a root watched folder to remove");
            return;
        }

        var selectedFolderPath = SelectedLocalTreeNode.FullPath;
        var selectedFolderToRemove = WatchedFolders.FirstOrDefault(f => 
            f.Path.Equals(selectedFolderPath, StringComparison.OrdinalIgnoreCase));
        
        if (selectedFolderToRemove != null)
        {
            WatchedFolders.Remove(selectedFolderToRemove);
            SaveSettings();
            AddLog($"Removed watched folder: {selectedFolderPath}");
            
            // Refresh the local files tree
            await RefreshLocalFilesAsync();
        }
    }

    #endregion
}
