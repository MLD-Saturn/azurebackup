using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureBackup.Core.Models;
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

    [RelayCommand]
    private void PauseBackup()
    {
        _orchestrator.Pause();
        AddLog("Backup paused");
    }

    [RelayCommand]
    private void ResumeBackup()
    {
        _orchestrator.Resume();
        AddLog("Backup resumed");
    }

    [RelayCommand]
    private async Task PerformFullScanAsync()
    {
        if (!IsInitialized)
        {
            AddLog("Please initialize first");
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

        // Generate preview showing ALL files (for full scan)
        AddLog("Analyzing all files for full scan...");
        IsOperationInProgress = true;
        CreateOperationCts();
        
        try
        {
            // For full scan, we create a preview that shows all files as "to be backed up"
            var config = _databaseService.GetConfiguration();
            OperationPreview preview = new()
            {
                OperationType = OperationType.Backup,
                OperationDescription = "Force full scan - re-upload ALL files (ignoring backup history)",
                SourceDescription = $"{config.WatchedFolders.Count(f => f.IsEnabled)} watched folder(s)",
                TargetDescription = $"Azure Storage ({config.ContainerName ?? "backup"})"
            };

            // Scan all watched folders to get file count
            List<string> allFiles = new();
            foreach (var folder in config.WatchedFolders.Where(f => f.IsEnabled))
            {
                _operationCts!.Token.ThrowIfCancellationRequested();
                var files = await _fileWatcherService.ScanFolderAsync(folder, _operationCts.Token);
                allFiles.AddRange(files);
            }

            // Add all files to the preview
            foreach (var filePath in allFiles)
            {
                try
                {
                    FileInfo fileInfo = new(filePath);
                    if (fileInfo.Exists)
                    {
                        preview.FilesToCreate.Add(new PreviewFileAction
                        {
                            FilePath = filePath,
                            FileSize = fileInfo.Length,
                            LastModified = fileInfo.LastWriteTime,
                            Action = FileActionType.Update,
                            Reason = "Force re-upload (full scan)"
                        });
                    }
                }
                catch { /* Skip inaccessible files */ }
            }

            // Show preview dialog
            IsOperationInProgress = false;
            
            if (!preview.HasChanges)
            {
                AddLog("No files found in watched folders");
                return;
            }

            var confirmed = await ShowPreviewDialogAsync(preview);

            if (!confirmed)
            {
                AddLog("Full scan cancelled by user");
                return;
            }

            // Proceed with full scan
            IsOperationInProgress = true;
            AddLog("?? Force Full Scan: Re-queuing ALL files (ignoring backup history)...");
            
            Progress<(int current, int total, string file)> progress = new(p =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    ProgressValue = (double)p.current / p.total * 100;
                    ProgressText = $"Scanning: {p.current}/{p.total} - {Path.GetFileName(p.file)}";
                });
            });

            await _orchestrator.PerformFullScanAsync(progress, _operationCts!.Token);
            RefreshStatistics();
            AddLog("Full scan complete - all files queued for backup");
            
            // Refresh local file tree
            await RefreshLocalFilesAsync();
        }
        catch (OperationCanceledException)
        {
            AddLog("Scan cancelled");
        }
        finally
        {
            IsOperationInProgress = false;
            StopProgressTracking();
        }
    }

    [RelayCommand]
    private void BackupSingleFile()
    {
        // Request the View to open a file picker dialog
        FilePickerRequested?.Invoke(this, EventArgs.Empty);
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
    private async Task ConfirmAndExecuteBackupAsync(List<string> filePaths)
    {
        IsOperationInProgress = true;
        CreateOperationCts();

        try
        {
            var result = await ConfirmAndFilterBackupFilesAsync(filePaths, _operationCts!.Token);
            if (result is null) return;

            await ExecuteBackupAsync(result.Value.files, result.Value.preview, _operationCts.Token);

            RefreshStatistics();
        }
        catch (OperationCanceledException)
        {
            AddLog("Backup cancelled");
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

        // Remove files the user excluded in the preview dialog
        var excluded = preview.ExcludedFilePaths;
        if (excluded.Count > 0)
        {
            filePaths = filePaths
                .Where(f => !excluded.Contains(f))
                .ToList();
        }

        if (filePaths.Count == 0)
        {
            AddLog("All files were excluded - nothing to backup");
            return null;
        }

        return (filePaths, preview);
    }

    /// <summary>
    /// Executes a batch backup with standard progress tracking.
    /// Uses aggregate byte tracking from the service layer for accurate parallel progress.
    /// </summary>
    private async Task ExecuteBackupAsync(
        List<string> filePaths,
        OperationPreview preview,
        CancellationToken cancellationToken)
    {
        var totalFiles = preview.IncludedCreateCount + preview.IncludedOverwriteCount;
        StartProgressTracking("Backing up", totalFiles, preview.TotalBytesToTransfer);

        Progress<(int fileIndex, int totalFiles, string fileName, long bytesProcessed, long totalBytes, long currentFileBytes, long currentFileSize)> progress = new(p =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Use aggregate bytesProcessed from the service — already handles parallel delta tracking
                UpdateOverallProgress(p.bytesProcessed, p.fileIndex + 1);
                UpdateCurrentFileDisplay(p.fileName, p.currentFileBytes, p.currentFileSize, p.fileIndex);
            });
        });

        await _orchestrator.BackupFilesAsync(filePaths, progress, cancellationToken);

        // Ensure bar reaches 100% at completion
        UpdateOverallProgress(preview.TotalBytesToTransfer, totalFiles);

        AddLog($"Successfully backed up {totalFiles} file(s)");

        await RefreshLocalFilesAsync();
        await RefreshRestorableFilesAsync();
    }

    #endregion
}
