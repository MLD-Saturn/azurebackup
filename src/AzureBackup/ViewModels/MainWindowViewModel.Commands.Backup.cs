using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core.Models;
using AzureBackup.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace AzureBackup.ViewModels;

/// <summary>
/// Backup commands for MainWindowViewModel.
/// </summary>
public partial class MainWindowViewModel
{
    #region Backup Commands

    [RelayCommand]
    private async Task StartBackupAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please initialize first");
            return;
        }

        // Check if there are any watched folders
        if (!WatchedFolders.Any(f => f.IsEnabled))
        {
            AddLog("Please add at least one watched folder in Settings");
            return;
        }

        // Check if blob service is connected
        if (!_blobService.IsConnected)
        {
            if (UseEntraIdAuth)
            {
                AddLog("Not connected to Azure Storage. Please sign in with Microsoft Entra ID in Settings.");
            }
            else
            {
                AddLog("Not connected to Azure Storage. Please configure your connection string in Settings.");
            }
            return;
        }

        // Generate preview of what will be backed up
        AddLog("Analyzing files for backup...");
        IsOperationInProgress = true;
        CreateOperationCts();

        try
        {
            var preview = await _orchestrator.PreviewBackupSyncAsync(_operationCts!.Token);

            // Show preview dialog if there are files to backup
            IsOperationInProgress = false;
            
            if (!preview.HasChanges)
            {
                AddLog("All files are up to date - no backup needed");
                return;
            }

            var confirmed = await ShowPreviewDialogAsync(preview);

            if (!confirmed)
            {
                AddLog("Backup cancelled by user");
                return;
            }

            // Proceed with sync
            IsOperationInProgress = true;
            AddLog("Starting backup sync...");
            
            Progress<(int current, int total, string file, string status)> syncProgress = new(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ProgressValue = (double)p.current / p.total * 100;
                    ProgressText = $"Checking: {p.current}/{p.total} - {p.file} ({p.status})";
                });
            });

            var syncResult = await _orchestrator.PerformInitialSyncAsync(syncProgress, _operationCts.Token);
            
            // Log sync summary
            if (syncResult.TotalToBackup > 0)
            {
                AddLog($"Sync complete: {syncResult.NewFilesQueued} new, {syncResult.ModifiedFilesQueued} modified, " +
                       $"{syncResult.UnchangedFiles} unchanged files");
            }
            else
            {
                AddLog($"All {syncResult.UnchangedFiles} files are up to date");
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("Sync cancelled");
            return;
        }
        finally
        {
            IsOperationInProgress = false;
            StopProgressTracking();
        }

        // Start the backup monitoring service
        _orchestrator.Start();
        IsBackupRunning = true;
        AddLog("Backup monitoring started - watching for file changes");
        RefreshStatistics();
        
        // Refresh local file tree to show updated status
        await RefreshLocalFilesAsync();
    }

    [RelayCommand]
    private async Task StopBackupAsync()
    {
        await _orchestrator.StopAsync();
        IsBackupRunning = false;
        AddLog("Backup monitoring stopped");
        RefreshStatistics();
    }

    /// <summary>
    /// Backs up files by their paths. Used by file picker, drag-drop, and external callers.
    /// </summary>
    public async Task BackupFilePathsAsync(IReadOnlyList<string> filePaths)
    {
        if (!IsInitialized)
        {
            AddLog("Please initialize first");
            return;
        }

        if (!_blobService.IsConnected)
        {
            AddLog("Not connected to Azure Storage. Please check your connection settings.");
            return;
        }

        if (filePaths.Count == 0) return;

        AddLog($"Preparing to backup {filePaths.Count} file(s)...");
        await ConfirmAndExecuteBackupAsync(filePaths.ToList());
    }

    #endregion

    #region Backup Helpers

    /// <summary>
    /// Full backup lifecycle: preview → confirm → filter exclusions → execute with progress → cleanup.
    /// All file-based backup entry points delegate to this method for consistent behavior.
    /// </summary>
    /// <param name="forceReupload">B43: when true, the orchestrator bypasses
    /// the metadata-skip fast path and the per-chunk dedup filter for every
    /// file in the input list. Used by the Sync tab's Force re-upload
    /// command to recover files whose remote bytes are corrupt or missing
    /// without re-checking via integrity-check first. Default false matches
    /// the historical Backup Selected behaviour.</param>
    private async Task ConfirmAndExecuteBackupAsync(List<string> filePaths, bool forceReupload = false)
    {
        IsOperationInProgress = true;
        CreateOperationCts();

        try
        {
            var result = await ConfirmAndFilterBackupFilesAsync(filePaths, _operationCts!.Token);
            if (result is null) return;

            await ExecuteBackupAsync(result.Value.files, result.Value.preview, forceReupload, _operationCts.Token);

            RefreshStatistics();
        }
        catch (OperationCanceledException)
        {
            AddLog("Backup cancelled");
            ProgressTab.MarkCancelled();
        }
        catch (Exception ex)
        {
            AddLog($"Backup failed: {ex.Message}");
        }
        finally
        {
            IsOperationInProgress = false;
            StopProgressTracking();
        }
    }

    /// <summary>
    /// Shows a backup preview dialog and filters out files the user excluded.
    /// Returns the filtered file list and the preview, or null if cancelled/no changes.
    /// </summary>
    /// <remarks>
    /// B31: the returned file list contains ONLY the files the orchestrator
    /// actually needs to back up (preview's <c>FilesToCreate</c> and
    /// <c>FilesToOverwrite</c> with <c>IsIncluded</c>=true). Files the preview
    /// classified as <c>FilesToSkip</c> (unchanged) are removed here, BEFORE
    /// being passed to <see cref="BackupOrchestrator.BackupFilesAsync"/>.
    /// <para/>
    /// Why this matters: the orchestrator computes its own <c>totalFiles</c>
    /// and <c>totalBytes</c> from whatever list it receives, and its internal
    /// metadata-skip path reports an unchanged file's full size as
    /// "completed bytes" the moment it is detected. If the unchanged files
    /// were left in the input list, the progress UI's numerator
    /// (<c>TotalBytesProcessed</c>, sourced from the orchestrator) would
    /// exceed its denominator (<c>TotalBytes</c>, sourced from the preview's
    /// <c>TotalBytesToTransfer</c>) and you would see e.g. "200 / 100 files"
    /// and "2 TB / 1 TB" with progress over 100%, plus a wildly wrong
    /// upload-speed estimate because the unchanged files' bytes flow into
    /// the running total essentially instantly. Pre-filtering here keeps the
    /// preview's "what we promised the user" set as the single source of
    /// truth for both numerator and denominator.
    /// </remarks>
    private async Task<(List<string> files, OperationPreview preview)?> ConfirmAndFilterBackupFilesAsync(
        List<string> filePaths,
        CancellationToken cancellationToken)
    {
        var preview = await _orchestrator.PreviewBackupFilesAsync(filePaths, cancellationToken);

        IsOperationInProgress = false;

        if (!preview.HasChanges)
        {
            AddLog("All selected files are already backed up - no changes needed");
            return null;
        }

        var confirmed = await ShowPreviewDialogAsync(preview);
        if (!confirmed)
        {
            AddLog("Backup cancelled by user");
            return null;
        }

        // B31: rebuild the file list from the preview's included-create and
        // included-overwrite sets. This naturally excludes:
        //   - Files the preview classified as FilesToSkip (unchanged) - they
        //     would otherwise drive the progress numerator past the denominator.
        //   - Files the user unchecked in the preview dialog (IsIncluded=false).
        // See xmldoc above for the bug this prevents.
        var includedFiles = preview.FilesToCreate.Where(f => f.IsIncluded)
            .Concat(preview.FilesToOverwrite.Where(f => f.IsIncluded))
            .Select(f => f.FilePath)
            .ToList();

        if (includedFiles.Count == 0)
        {
            AddLog("All files were excluded - nothing to backup");
            return null;
        }

        return (includedFiles, preview);
    }

    /// <summary>
    /// Executes a batch backup with progress tracking on the Progress tab.
    /// Uses aggregate byte tracking from the service layer for accurate parallel progress.
    /// Reports per-file start/progress/complete events to <see cref="ProgressTab"/>.
    /// </summary>
    private async Task ExecuteBackupAsync(
        List<string> filePaths,
        OperationPreview preview,
        bool forceReupload,
        CancellationToken cancellationToken)
    {
        var totalFiles = preview.IncludedCreateCount + preview.IncludedOverwriteCount;
        var totalBytes = preview.TotalBytesToTransfer;

        // Compute small-file grouping from preview data
        const long SmallFileThreshold = RestoreService.SmallFileThresholdBytes;
        var includedFiles = preview.FilesToCreate.Where(f => f.IsIncluded)
            .Concat(preview.FilesToOverwrite.Where(f => f.IsIncluded))
            .ToList();
        var smallFileCount = includedFiles.Count(f => f.FileSize <= SmallFileThreshold);
        var smallFileTotalBytes = includedFiles.Where(f => f.FileSize <= SmallFileThreshold).Sum(f => f.FileSize);

        // Initialize progress tab and auto-switch
        StartProgressTab("Backing up", totalFiles, totalBytes, smallFileCount, smallFileTotalBytes);

        // Also keep legacy tracking for tray tooltip
        StartProgressTracking("Backing up", totalFiles, totalBytes);

        // Track which files have started/completed for per-file row management
        var startedFiles = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        var completedFiles = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        int smallFilesCompleted = 0;
        long smallFilesBytesProcessed = 0;

        Progress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)> progress = new(p =>
        {
            var isSmall = p.currentFileSize <= SmallFileThreshold;

            // Report file started on first callback for this file (large files only)
            if (startedFiles.TryAdd(p.fileIndex, 0) && !isSmall)
            {
                ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                {
                    Type = AzureBackup.Core.Models.OperationProgressType.FileStarted,
                    FileIndex = p.fileIndex,
                    FileName = p.fileName,
                    FileSize = p.currentFileSize,
                    FileStatus = AzureBackup.Core.Models.FileOperationStatus.Uploading,
                    TotalBytesProcessed = p.bytesProcessed,
                    TotalBytes = p.totalBytes,
                    TotalFilesCompleted = completedFiles.Count,
                    TotalFiles = p.totalFiles
                });
            }

            // Report byte progress for large files only
            if (!isSmall)
            {
                ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                {
                    Type = AzureBackup.Core.Models.OperationProgressType.FileProgress,
                    FileIndex = p.fileIndex,
                    FileName = p.fileName,
                    FileSize = p.currentFileSize,
                    FileBytesProcessed = p.currentFileBytes,
                    FileStatus = AzureBackup.Core.Models.FileOperationStatus.Uploading,
                    TotalBytesProcessed = p.bytesProcessed,
                    TotalBytes = p.totalBytes,
                    TotalFilesCompleted = completedFiles.Count,
                    TotalFiles = p.totalFiles
                });
            }

            // Detect file completion
            if (p.currentFileBytes >= p.currentFileSize && completedFiles.TryAdd(p.fileIndex, 0))
            {
                if (isSmall)
                {
                    var sc = Interlocked.Increment(ref smallFilesCompleted);
                    var sb = Interlocked.Add(ref smallFilesBytesProcessed, p.currentFileSize);
                    ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                    {
                        Type = AzureBackup.Core.Models.OperationProgressType.SmallFileGroupProgress,
                        SmallFilesCompleted = sc,
                        SmallFilesTotal = smallFileCount,
                        SmallFilesBytesProcessed = sb,
                        SmallFilesTotalBytes = smallFileTotalBytes,
                        TotalBytesProcessed = p.bytesProcessed,
                        TotalBytes = p.totalBytes,
                        TotalFilesCompleted = completedFiles.Count,
                        TotalFiles = p.totalFiles
                    });
                }
                else
                {
                    ProgressTab.ReportProgress(new AzureBackup.Core.Models.OperationProgressReport
                    {
                        Type = AzureBackup.Core.Models.OperationProgressType.FileCompleted,
                        FileIndex = p.fileIndex,
                        FileName = p.fileName,
                        FileSize = p.currentFileSize,
                        FileBytesProcessed = p.currentFileSize,
                        FileStatus = AzureBackup.Core.Models.FileOperationStatus.Complete,
                        TotalBytesProcessed = p.bytesProcessed,
                        TotalBytes = p.totalBytes,
                        TotalFilesCompleted = completedFiles.Count,
                        TotalFiles = p.totalFiles
                    });
                }
            }
        });

        await _orchestrator.BackupFilesAsync(filePaths, progress, forceReupload, cancellationToken);

        // Show completion summary
        ProgressTab.CompleteOperation(completedFiles.Count, totalFiles - completedFiles.Count, 0, totalBytes);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => CurrentView = "Progress");

        AddLog($"Successfully backed up {totalFiles} file(s)");

        await RefreshBothFilePanesAsync();
    }

    #endregion
}
